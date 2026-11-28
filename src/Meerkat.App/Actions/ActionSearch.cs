namespace Meerkat.App.Actions;

/// <summary>How well a text fits what was typed, and which of its letters were typed.</summary>
public sealed record ActionMatch(int Score, IReadOnlyList<int> Positions);

/// <summary>
/// The search of the action palette. What is typed need not be a word of the title: its letters only
/// have to occur in that order, so "rst" finds "Restart container". Letters that follow each other or
/// start a word count for more, which puts the title somebody most likely meant first. Letters that do
/// neither are scattered by chance; more than one of them and the title is not what was meant.
/// </summary>
public static class ActionSearch
{
    private const int LetterScore = 1;
    private const int WordStartBonus = 8;

    // More than a word start, so that a word typed out beats the initials of several.
    private const int ConsecutiveBonus = 10;
    private const int MaxScatteredLetters = 1;
    private const int FirstLetterBonus = 4;

    /// <summary>Returns null when the text does not hold the letters of the query in their order.</summary>
    public static ActionMatch? Match(string text, string query)
    {
        var letters = query.Where(character => !char.IsWhiteSpace(character)).Select(char.ToLowerInvariant).ToArray();
        if (letters.Length == 0)
        {
            return new ActionMatch(0, []);
        }

        if (letters.Length > text.Length)
        {
            return null;
        }

        // best[i, j]: the highest score of typing the first i + 1 letters with letter i found at position j.
        var best = new int[letters.Length, text.Length];
        var previous = new int[letters.Length, text.Length];
        for (var letter = 0; letter < letters.Length; letter++)
        {
            for (var position = 0; position < text.Length; position++)
            {
                best[letter, position] = int.MinValue;
                if (char.ToLowerInvariant(text[position]) != letters[letter])
                {
                    continue;
                }

                var own = LetterScore + (IsWordStart(text, position) ? WordStartBonus : 0) + (position == 0 ? FirstLetterBonus : 0);
                if (letter == 0)
                {
                    best[letter, position] = own;
                    continue;
                }

                for (var before = letter - 1; before < position; before++)
                {
                    if (best[letter - 1, before] == int.MinValue)
                    {
                        continue;
                    }

                    var score = best[letter - 1, before] + own + (before == position - 1 ? ConsecutiveBonus : 0);
                    if (score > best[letter, position])
                    {
                        best[letter, position] = score;
                        previous[letter, position] = before;
                    }
                }
            }
        }

        var last = letters.Length - 1;
        var end = -1;
        for (var position = 0; position < text.Length; position++)
        {
            if (best[last, position] != int.MinValue && (end < 0 || best[last, position] > best[last, end]))
            {
                end = position;
            }
        }

        if (end < 0)
        {
            return null;
        }

        var positions = new int[letters.Length];
        for (int letter = last, position = end; letter >= 0; position = previous[letter, position], letter--)
        {
            positions[letter] = position;
        }

        var scattered = positions.Where((position, letter) => !IsWordStart(text, position) && (letter == 0 || positions[letter - 1] != position - 1)).Count();
        return scattered > MaxScatteredLetters ? null : new ActionMatch(best[last, end], positions);
    }

    private static bool IsWordStart(string text, int position) =>
        position == 0 || !char.IsLetterOrDigit(text[position - 1]) || (char.IsUpper(text[position]) && char.IsLower(text[position - 1]));
}
