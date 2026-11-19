using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;

namespace Snit.Core;

/// <summary>
/// Works out FFmpeg-style formulas (like "clip((P-X/W)/0.03,0,1)") in C#, so the preview can
/// draw exactly what FFmpeg will make in the export from the very same text. Formulas are turned
/// into fast .NET code once and kept.
///
/// Knows the variables X, Y, W, H (pixel and size), P and Q (how far a transition is, 0..1),
/// T (seconds), PI and E; the operators + - * / ^; and FFmpeg's functions abs, floor, ceil,
/// trunc, round, mod, hypot, atan2, atan, asin, acos, sin, cos, tan, sqrt, pow, exp, log, min,
/// max, clip, if, ifnot, lt, lte, gt, gte, eq, between, not.
/// </summary>
public static class Formula
{
    public delegate double Compiled(double x, double y, double w, double h, double p, double q, double t);

    private static readonly ConcurrentDictionary<string, Compiled> Cache = new();

    public static Compiled Compile(string text) => Cache.GetOrAdd(text, Build);

    public static double Evaluate(string text, double x = 0, double y = 0, double w = 1, double h = 1, double p = 0, double q = 0, double t = 0)
        => Compile(text)(x, y, w, h, p, q, t);

    private static Compiled Build(string text)
    {
        var names = new[] { "X", "Y", "W", "H", "P", "Q", "T" };
        var parameters = names.Select(n => Expression.Parameter(typeof(double), n)).ToArray();
        var parser = new Parser(text, parameters);
        Expression body = parser.ParseAll();
        return Expression.Lambda<Compiled>(body, parameters).Compile();
    }

    private sealed class Parser(string text, ParameterExpression[] variables)
    {
        private int pos;

        public Expression ParseAll()
        {
            Expression e = Sum();
            Skip();
            if (pos < text.Length) throw new FormatException($"Unexpected '{text[pos]}' at {pos} in: {text}");
            return e;
        }

        private void Skip() { while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++; }

        private bool Take(char c)
        {
            Skip();
            if (pos < text.Length && text[pos] == c) { pos++; return true; }
            return false;
        }

        private Expression Sum()
        {
            Expression left = Product();
            while (true)
            {
                if (Take('+')) left = Expression.Add(left, Product());
                else if (Take('-')) left = Expression.Subtract(left, Product());
                else return left;
            }
        }

        private Expression Product()
        {
            Expression left = Unary();
            while (true)
            {
                if (Take('*')) left = Expression.Multiply(left, Unary());
                else if (Take('/')) left = Expression.Divide(left, Unary());
                else return left;
            }
        }

        private Expression Unary()
        {
            if (Take('-')) return Expression.Negate(Unary());
            if (Take('+')) return Unary();
            return Power();
        }

        private Expression Power()
        {
            Expression b = Atom();
            if (Take('^')) return Call("pow", [b, Unary()]);
            return b;
        }

        private Expression Atom()
        {
            Skip();
            if (Take('('))
            {
                Expression inner = Sum();
                if (!Take(')')) throw new FormatException("Missing ) in: " + text);
                return inner;
            }
            int start = pos;
            if (pos < text.Length && (char.IsDigit(text[pos]) || text[pos] == '.'))
            {
                while (pos < text.Length && (char.IsDigit(text[pos]) || text[pos] == '.')) pos++;
                if (pos < text.Length && (text[pos] == 'e' || text[pos] == 'E') && pos + 1 < text.Length &&
                    (char.IsDigit(text[pos + 1]) || text[pos + 1] == '-' || text[pos + 1] == '+'))
                {
                    pos += 2;
                    while (pos < text.Length && char.IsDigit(text[pos])) pos++;
                }
                return Expression.Constant(double.Parse(text[start..pos], CultureInfo.InvariantCulture));
            }
            while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_')) pos++;
            string name = text[start..pos];
            if (name.Length == 0) throw new FormatException($"Unexpected '{(pos < text.Length ? text[pos] : ' ')}' in: {text}");

            if (Take('('))
            {
                var args = new List<Expression>();
                if (!Take(')'))
                {
                    do args.Add(Sum()); while (Take(','));
                    if (!Take(')')) throw new FormatException("Missing ) in: " + text);
                }
                return Call(name, args);
            }
            if (name == "PI") return Expression.Constant(Math.PI);
            if (name == "E") return Expression.Constant(Math.E);
            ParameterExpression? v = variables.FirstOrDefault(p => p.Name == name);
            return v ?? throw new FormatException($"Unknown name '{name}' in: {text}");
        }

        private static readonly Expression Zero = Expression.Constant(0.0), One = Expression.Constant(1.0);

        private static Expression Math1(string method, Expression a) => Expression.Call(typeof(Math).GetMethod(method, [typeof(double)])!, a);
        private static Expression Math2(string method, Expression a, Expression b)
            => Expression.Call(typeof(Math).GetMethod(method, [typeof(double), typeof(double)])!, a, b);
        private static Expression Bool(Expression condition) => Expression.Condition(condition, One, Zero);
        private static Expression IsTrue(Expression value) => Expression.NotEqual(value, Zero);

        private static Expression Call(string name, IReadOnlyList<Expression> a)
        {
            Expression A(int i) => i < a.Count ? a[i] : throw new FormatException($"{name} needs more numbers");
            return name switch
            {
                "abs" => Math1("Abs", A(0)),
                "floor" => Math1("Floor", A(0)),
                "ceil" => Math1("Ceiling", A(0)),
                "trunc" => Math1("Truncate", A(0)),
                "round" => Expression.Call(typeof(Math).GetMethod("Round", [typeof(double), typeof(MidpointRounding)])!, A(0),
                    Expression.Constant(MidpointRounding.AwayFromZero)),
                "sqrt" => Math1("Sqrt", A(0)),
                "exp" => Math1("Exp", A(0)),
                "log" => Math1("Log", A(0)),
                "sin" => Math1("Sin", A(0)),
                "cos" => Math1("Cos", A(0)),
                "tan" => Math1("Tan", A(0)),
                "atan" => Math1("Atan", A(0)),
                "asin" => Math1("Asin", A(0)),
                "acos" => Math1("Acos", A(0)),
                "pow" => Math2("Pow", A(0), A(1)),
                "atan2" => Math2("Atan2", A(0), A(1)),
                "hypot" => Math1("Sqrt", Expression.Add(Expression.Multiply(A(0), A(0)), Expression.Multiply(A(1), A(1)))),
                "min" => Math2("Min", A(0), A(1)),
                "max" => Math2("Max", A(0), A(1)),
                // FFmpeg's mod: x - y*floor(x/y), so the answer has the sign of y.
                "mod" => Expression.Subtract(A(0), Expression.Multiply(A(1), Math1("Floor", Expression.Divide(A(0), A(1))))),
                "clip" => Math2("Max", A(1), Math2("Min", A(2), A(0))),
                "lt" => Bool(Expression.LessThan(A(0), A(1))),
                "lte" => Bool(Expression.LessThanOrEqual(A(0), A(1))),
                "gt" => Bool(Expression.GreaterThan(A(0), A(1))),
                "gte" => Bool(Expression.GreaterThanOrEqual(A(0), A(1))),
                "eq" => Bool(Expression.Equal(A(0), A(1))),
                "between" => Bool(Expression.AndAlso(Expression.GreaterThanOrEqual(A(0), A(1)), Expression.LessThanOrEqual(A(0), A(2)))),
                "not" => Bool(Expression.Equal(A(0), Zero)),
                "if" => Expression.Condition(IsTrue(A(0)), A(1), a.Count > 2 ? a[2] : Zero),
                "ifnot" => Expression.Condition(IsTrue(A(0)), a.Count > 2 ? a[2] : Zero, A(1)),
                _ => throw new FormatException($"Unknown function '{name}'"),
            };
        }
    }
}
