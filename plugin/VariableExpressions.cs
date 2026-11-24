using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TFlex.Model;

namespace TFlexMcp
{
    internal static class VariableExpressions
    {
        public static void Set(Variable variable, string requested)
        {
            string normalized = WithoutLayout(requested, validateParentheses: true);
            string previous = variable.Expression;
            variable.Expression = requested;
            string current = variable.Expression;
            if (string.Equals(current, previous, StringComparison.Ordinal) &&
                !string.Equals(WithoutLayout(current), normalized, StringComparison.Ordinal) &&
                !SameNumber(current, requested, CultureInfo.InvariantCulture) &&
                !SameNumber(current, requested, CultureInfo.CurrentCulture))
                throw new InvalidOperationException("T-FLEX did not apply the expression for " + variable.Name + ".");
        }

        public static HashSet<string> ReadDiagnosticIds(Document doc)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var message in doc.Diagnostics) ids.Add(message.Id.ToString());
            return ids;
        }

        public static ChangesResult RebuildAndCommit(Document doc, IEnumerable<Variable> changed,
            HashSet<string> previousDiagnostics, out string stage)
        {
            // Check diagnostics emitted by the assignments while the block can still be canceled.
            stage = null;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var variable in changed)
            {
                string error = variable.ErrorString;
                if (!string.IsNullOrWhiteSpace(error))
                    throw new InvalidOperationException(variable.Name + ": " + error);
                ids.Add(variable.ObjectId.ToString());
            }
            foreach (var message in doc.Diagnostics)
            {
                if (message.Type != DiagnosticsMessageType.Information && message.Object != null &&
                    ids.Contains(message.Object.ObjectId.ToString()) &&
                    !previousDiagnostics.Contains(message.Id.ToString()))
                    throw new InvalidOperationException(message.Message);
            }
            stage = "EndChanges";
            return doc.EndChanges(new EndChangesOptions
            {
                Unconditionally = false, Regenerate = true, ShowLastErrorDialog = false, Merge = false
            });
        }

        private static bool SameNumber(string left, string right, CultureInfo culture)
        {
            double a;
            double b;
            return double.TryParse(left, NumberStyles.Float, culture, out a)
                && double.TryParse(right, NumberStyles.Float, culture, out b) && a == b;
        }

        private static string WithoutLayout(string expression, bool validateParentheses = false)
        {
            if (expression == null) return null;
            var result = new StringBuilder(expression.Length);
            char quotation = '\0';
            int parentheses = 0;
            for (int i = 0; i < expression.Length; i++)
            {
                char current = expression[i];
                if (quotation != '\0')
                {
                    result.Append(current);
                    if (current == '\\' && i + 1 < expression.Length)
                        result.Append(expression[++i]);
                    else if (current == quotation)
                        quotation = '\0';
                }
                else if (current == '"' || current == '\'')
                {
                    quotation = current;
                    result.Append(current);
                }
                else if (!char.IsWhiteSpace(current))
                {
                    if (current == '(') parentheses++;
                    if (current == ')') parentheses--;
                    if (validateParentheses && parentheses < 0)
                        throw new ArgumentException("Unbalanced parentheses in the expression.");
                    result.Append(current);
                }
            }
            if (validateParentheses && parentheses != 0)
                throw new ArgumentException("Unbalanced parentheses in the expression.");
            return result.ToString();
        }
    }
}
