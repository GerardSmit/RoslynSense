namespace SampleProject.Ranking;

/// <summary>
/// Lives in a namespace nothing imports, so completion has to find it through import completion
/// and add the using on commit.
/// </summary>
public static class RankingExtensions
{
    public static string ShoutRanking(this string value) => value.ToUpperInvariant();
}
