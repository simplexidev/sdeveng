namespace SdevEng.Tests;

public class PromptReturnStatusTests
{
    [Theory]
    [InlineData("SUCCESS")]
    [InlineData("WAITING_FOR_REVIEW")]
    [InlineData("BLOCKED")]
    [InlineData("OUT_OF_USAGE")]
    [InlineData("FAILURE")]
    public void ParsesEachDefinedStatus(string status)
    {
        Assert.Equal(Enum.Parse<PromptReturnStatus>(status), PromptReturnStatusContract.Parse($"Response text\nPROMPT_RETURN_STATUS: {status}\n  \t"));
    }

    [Theory]
    [InlineData("Response without marker")]
    [InlineData("PROMPT_RETURN_STATUS: SUCCESS\nPROMPT_RETURN_STATUS: FAILURE")]
    [InlineData("PROMPT_RETURN_STATUS: SUCCESS\nMore text")]
    [InlineData("PROMPT_RETURN_STATUS: UNKNOWN")]
    [InlineData("PROMPT_RETURN_STATUS: SUCCESS extra")]
    [InlineData("PROMPT_RETURN_STATUS: success")]
    [InlineData("Text PROMPT_RETURN_STATUS: SUCCESS")]
    public void RejectsMissingDuplicateInvalidAndNonterminalMarkers(string response)
    {
        Assert.Throws<FormatException>(() => PromptReturnStatusContract.Parse(response));
    }
}
