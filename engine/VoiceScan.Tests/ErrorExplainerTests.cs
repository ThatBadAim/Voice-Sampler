using VoiceScan.App.Core.Services;

namespace VoiceScan.Tests;

public class ErrorExplainerTests
{
    [Fact]
    public void FileNotFound_NamesTheMissingFile()
    {
        var e = ErrorExplainer.Explain(new FileNotFoundException("x", "model.onnx"));
        Assert.Contains("model.onnx", e.Cause);
    }

    [Fact]
    public void WrapperExceptions_AreUnwrappedToTheRealCause()
    {
        var wrapped = new AggregateException(new TypeInitializationException("T", new UnauthorizedAccessException("denied")));
        Assert.Contains("not allowed", ErrorExplainer.Explain(wrapped).Cause);
    }

    [Fact]
    public void DiskFull_IsRecognisedFromMessage()
    {
        Assert.Contains("disk is full", ErrorExplainer.Explain(new IOException("No space left on device")).Cause);
    }

    [Fact]
    public void UnknownException_StillGivesCauseAndRemedy()
    {
        var e = ErrorExplainer.Explain(new InvalidOperationException("boom"));
        Assert.Contains("InvalidOperationException", e.Cause);
        Assert.Contains("boom", e.Cause);
        Assert.False(string.IsNullOrWhiteSpace(e.Remedy));
    }
}
