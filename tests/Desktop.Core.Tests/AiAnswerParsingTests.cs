using Desktop.Core;
using Xunit;

namespace Desktop.Core.Tests;

public sealed class AiAnswerParsingTests
{
    private static StudyQuestion Choice => new("q", "single", "题干", ["甲", "乙"], 0);

    [Theory]
    [InlineData("```json\n{\"choices\":[1],\"blanks\":[]}\n```")]
    [InlineData("  ```JSON\r\n{\"choices\":[1]}\r\n```  ")]
    [InlineData("```\n{\"choices\":[1]}\n```")]
    public void A_single_complete_json_code_block_preserves_the_answer(string response)
        => Assert.Equal([1], AiAnswer.Parse(response, Choice).Choices);

    [Theory]
    [InlineData("答案如下：\n```json\n{\"choices\":[1]}\n```")]
    [InlineData("```json\n{\"choices\":[1]}\n```\n这就是答案")]
    [InlineData("```json\n{\"choices\":[1]}")]
    [InlineData("```json\n{\"choices\":[0,1]}\n```")]
    [InlineData("```json\n{\"choices\":[9]}\n```")]
    public void Explanation_partial_blocks_and_invalid_answers_remain_rejected(string response)
        => Assert.ThrowsAny<FormatException>(() => AiAnswer.Parse(response, Choice));
}
