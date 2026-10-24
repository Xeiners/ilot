using System.Globalization;
using MyNotch.Localization;

namespace MyNotch.Modules.Search;

/// <summary>
/// The search's calculator: "18*1,2", "= (3 + 4)²"… The four operations (also × ÷ x), powers (^, ², ³), percentages
/// ("200 * 15%"), parentheses, √ / sqrt and π / pi. A comma or a point for decimals, spaces anywhere.
/// </summary>
public static class Calculator
{
    /// <summary>
    /// The value of <paramref name="text"/> when it is a calculation (an operation, at least, or it starts with "=");
    /// <c>null</c> for anything else, a plain number included.
    /// </summary>
    public static double? Evaluate(string text)
    {
        string expression = text.Trim();
        bool asked = expression.StartsWith('=');
        if (asked)
        {
            expression = expression[1..];
        }

        if (expression.Length == 0 || (!asked && !LooksLikeCalculation(expression)))
        {
            return null;
        }

        try
        {
            var parser = new Parser(expression);
            double value = parser.ParseExpression();
            return parser.AtEnd && double.IsFinite(value) ? value : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>"21,6", "1 234 567,89" in French; "21.6", "1,234,567.89" in English.</summary>
    public static string Format(double value)
    {
        if (Math.Abs(value) >= 1e15 || (value != 0 && Math.Abs(value) < 1e-9))
        {
            return value.ToString("0.#########E+0", Loc.Culture);
        }

        return Math.Round(value, 10).ToString("#,0.##########", Loc.Culture);
    }

    /// <summary>
    /// Only digits and operators, with a digit and an operator between two operands: not a word with a dash, nor a date
    /// or a file name ("12-03", "2026-10": a lone dash with no spaces).
    /// </summary>
    private static bool LooksLikeCalculation(string expression)
    {
        if (!expression.Contains(' ') && expression.Count(c => c is '+' or '*' or '/' or '^' or '×' or '÷' or '%' or '²' or '³' or '√' or '(' or 'x' or 'X') == 0)
        {
            return false;
        }

        bool digit = false, operation = false;
        foreach (char c in expression)
        {
            if (char.IsDigit(c))
            {
                digit = true;
            }
            else if (c is '+' or '*' or '/' or '^' or '×' or '÷' or '%' or '²' or '³' or '√' or '(' or ')')
            {
                operation = true;
            }
            else if (c is '-' or 'x' or 'X')
            {
                operation |= digit;
            }
            else if (c is not (' ' or ',' or '.' or 'π') && !char.IsLetter(c))
            {
                return false;
            }
            else if (char.IsLetter(c) && c is not ('x' or 'X'))
            {
                // Only the names of the functions it knows.
                if (!(expression.Contains("sqrt", StringComparison.OrdinalIgnoreCase) || expression.Contains("pi", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
        }

        return digit && operation;
    }

    /// <summary>Recursive descent: sums of products of powers of signed factors.</summary>
    private sealed class Parser(string text)
    {
        private int _at;

        public bool AtEnd
        {
            get
            {
                SkipSpaces();
                return _at == text.Length;
            }
        }

        public double ParseExpression()
        {
            double value = ParseTerm();
            while (true)
            {
                if (Take('+'))
                {
                    value += ParseTerm();
                }
                else if (Take('-'))
                {
                    value -= ParseTerm();
                }
                else
                {
                    return value;
                }
            }
        }

        private double ParseTerm()
        {
            double value = ParsePower();
            while (true)
            {
                if (Take('*') || Take('×') || Take('x') || Take('X'))
                {
                    value *= ParsePower();
                }
                else if (Take('/') || Take('÷'))
                {
                    double divisor = ParsePower();
                    if (divisor == 0)
                    {
                        throw new FormatException("Division by zero.");
                    }

                    value /= divisor;
                }
                else
                {
                    return value;
                }
            }
        }

        private double ParsePower()
        {
            double value = ParseUnary();
            if (Take('^'))
            {
                return Math.Pow(value, ParsePower()); // Right to left: 2^3^2 = 2^9.
            }

            return value;
        }

        private double ParseUnary()
        {
            if (Take('-'))
            {
                return -ParseUnary();
            }

            if (Take('+'))
            {
                return ParseUnary();
            }

            return ParsePostfix(ParsePrimary());
        }

        private double ParsePostfix(double value)
        {
            while (true)
            {
                if (Take('²'))
                {
                    value *= value;
                }
                else if (Take('³'))
                {
                    value = value * value * value;
                }
                else if (Take('%'))
                {
                    value /= 100;
                }
                else
                {
                    return value;
                }
            }
        }

        private double ParsePrimary()
        {
            if (Take('('))
            {
                double inner = ParseExpression();
                if (!Take(')'))
                {
                    throw new FormatException("Missing ).");
                }

                return inner;
            }

            if (Take('√') || TakeWord("sqrt"))
            {
                double radicand = ParseUnary();
                return radicand >= 0 ? Math.Sqrt(radicand) : throw new FormatException("Negative root.");
            }

            if (Take('π') || TakeWord("pi"))
            {
                return Math.PI;
            }

            return ParseNumber();
        }

        private double ParseNumber()
        {
            SkipSpaces();
            int start = _at;
            while (_at < text.Length && (char.IsDigit(text[_at]) || text[_at] is ',' or '.'))
            {
                _at++;
            }

            string number = text[start.._at].Replace(',', '.');
            if (number.Length == 0 || !double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value))
            {
                throw new FormatException("Number expected.");
            }

            return value;
        }

        private bool Take(char c)
        {
            SkipSpaces();
            if (_at < text.Length && text[_at] == c)
            {
                _at++;
                return true;
            }

            return false;
        }

        private bool TakeWord(string word)
        {
            SkipSpaces();
            if (string.Compare(text, _at, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                _at += word.Length;
                return true;
            }

            return false;
        }

        private void SkipSpaces()
        {
            while (_at < text.Length && text[_at] == ' ')
            {
                _at++;
            }
        }
    }
}
