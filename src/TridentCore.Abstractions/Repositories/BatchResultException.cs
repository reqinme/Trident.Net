namespace TridentCore.Abstractions.Repositories;

public class BatchResultException<TIdentifier>(IReadOnlyDictionary<TIdentifier, Exception> failures)
    : Exception($"Batch failed for {failures.Count} item(s): {Describe(failures)}")
{
    public IReadOnlyDictionary<TIdentifier, Exception> Failures { get; } = failures;

    // NOTE: 每个键的异常都要进消息体。只列出键时，部署失败会是一个
    //  没有任何原因可查的错误，调用方与用户都无法定位。
    private static string Describe(IReadOnlyDictionary<TIdentifier, Exception> failures) =>
        string.Join("; ", failures.Select(x => $"{x.Key} ({x.Value.Message})"));
}
