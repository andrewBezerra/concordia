using Concordia.Hook.Review;

namespace Concordia.Hook.Commands;

/// <summary>
/// Merges the JSON answers of the reviewer agents into one compact list and a verdict, so the main
/// agent does not spend tokens (or vary between runs) doing it by hand.
/// </summary>
internal static class ReviewMergeCommand
{
    public static int Run(string[] files, TextWriter stdout, TextWriter stderr)
    {
        if (files.Length == 0)
        {
            stderr.WriteLine("concordia-hook review-merge: usage: review-merge <file>...");
            return 1;
        }

        var inputs = files.Select(Read).ToList();
        stdout.WriteLine(ReviewMerger.Merge(inputs).Render());
        return 0;
    }

    private static ReviewInput Read(string path)
    {
        try
        {
            return new ReviewInput(path, File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ReviewInput(path, null);
        }
    }
}
