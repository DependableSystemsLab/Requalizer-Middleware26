using System.Collections.Generic;
using System.Text;

namespace OneOS.Common
{
    // Splits a shell command line into words: whitespace separates words; single and double quotes group
    // (quotes are removed); a backslash escapes the next character outside single quotes.
    public static class CommandLine
    {
        public static List<string> Split(string line)
        {
            var words = new List<string>();
            var word = new StringBuilder();
            bool inWord = false;
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else if (c == '\\' && quote == '"' && i + 1 < line.Length && line[i + 1] is '"' or '\\') word.Append(line[++i]);
                    else word.Append(c);
                }
                else if (c is '"' or '\'') { quote = c; inWord = true; }
                else if (c == '\\' && i + 1 < line.Length) { word.Append(line[++i]); inWord = true; }
                else if (char.IsWhiteSpace(c))
                {
                    if (inWord) { words.Add(word.ToString()); word.Clear(); inWord = false; }
                }
                else { word.Append(c); inWord = true; }
            }
            if (inWord) words.Add(word.ToString());
            return words;
        }
    }
}
