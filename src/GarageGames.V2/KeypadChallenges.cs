using System.Text;

namespace GarageGames.V2;

// One message a keypad event can show, with the code that answers it.
public sealed class KeypadChallengeDefinition
{
    public required string Prompt { get; set; }
    public required string Answer { get; set; }
}

// The message pool loaded from the keypad answer CSV, plus where it came from and any
// problem loading it, so Setup can report it instead of the app failing to start.
public sealed record KeypadChallengeSet(IReadOnlyList<KeypadChallengeDefinition> Challenges, string? Source, string? Error)
{
    public static KeypadChallengeSet Empty { get; } = new([], null, null);

    public static KeypadChallengeSet LoadOrReportError(string path)
    {
        if (!File.Exists(path))
        {
            return new KeypadChallengeSet([], path, $"Keypad answer file '{path}' was not found.");
        }

        try
        {
            // Excel keeps an open workbook's CSV locked for writing; share access to read it anyway.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return new KeypadChallengeSet(KeypadChallengeCsv.Parse(reader.ReadToEnd()), path, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new KeypadChallengeSet([], path, exception.Message);
        }
    }
}

// The keypad answer grid: the header row holds column labels (1-16), the first column holds
// row labels (A-D), and each cell's text is a message whose code is its row label then its
// column label ("rocket pepper 1819" in row A, column 2 answers A2). A row labelled "##"
// answers "##" for every message in it. Blank rows and cells are skipped.
public static class KeypadChallengeCsv
{
    public const string AnyColumnRowLabel = "##";

    public static List<KeypadChallengeDefinition> Parse(string text)
    {
        var rows = ReadRows(text);
        if (rows.Count == 0)
        {
            throw new InvalidDataException("The keypad answer file is empty.");
        }

        var columnLabels = rows[0].Select(cell => cell.Trim()).ToList();
        var challenges = new List<KeypadChallengeDefinition>();
        var seenPrompts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var rowLabel = row.Count > 0 ? row[0].Trim().ToUpperInvariant() : "";
            var hasText = row.Skip(1).Any(cell => cell.Trim().Length > 0);
            if (rowLabel.Length == 0)
            {
                if (hasText)
                {
                    throw new InvalidDataException($"Row {rowIndex + 1} of the keypad answer file has messages but no row label.");
                }
                continue;
            }

            for (var columnIndex = 1; columnIndex < row.Count; columnIndex++)
            {
                var prompt = row[columnIndex].Trim();
                if (prompt.Length == 0)
                {
                    continue;
                }

                var columnLabel = columnIndex < columnLabels.Count ? columnLabels[columnIndex].ToUpperInvariant() : "";
                if (rowLabel != AnyColumnRowLabel && columnLabel.Length == 0)
                {
                    throw new InvalidDataException($"'{prompt}' (row {rowIndex + 1}) is in a column with no label.");
                }

                var answer = rowLabel == AnyColumnRowLabel ? AnyColumnRowLabel : rowLabel + columnLabel;
                if (!MasterProtocolCodec.IsValidKeypadEntry(answer) || answer.Length == 0)
                {
                    throw new InvalidDataException(
                        $"'{prompt}' would need the code '{answer}', which the keypad cannot type (use 0-9, A-D, and #, up to {MasterProtocolCodec.MaximumKeypadEntryLength} keys).");
                }
                if (prompt.Length > 120)
                {
                    throw new InvalidDataException($"'{prompt[..40]}…' is longer than 120 characters.");
                }
                if (seenPrompts.TryGetValue(prompt, out var otherAnswer))
                {
                    throw new InvalidDataException($"'{prompt}' appears twice in the keypad answer file ({otherAnswer} and {answer}); each message needs one code.");
                }

                seenPrompts[prompt] = answer;
                challenges.Add(new KeypadChallengeDefinition { Prompt = prompt, Answer = answer });
            }
        }

        if (challenges.Count == 0)
        {
            throw new InvalidDataException("The keypad answer file has no messages.");
        }

        return challenges;
    }

    // Minimal RFC 4180 reader: commas separate cells, double quotes wrap cells that contain
    // commas, quotes, or line breaks, and "" inside quotes is a literal quote.
    private static List<List<string>> ReadRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < text.Length && text[index + 1] == '"')
                {
                    cell.Append('"');
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(character);
                }
                continue;
            }

            switch (character)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                default:
                    cell.Append(character);
                    break;
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
