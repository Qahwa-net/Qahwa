using Qahwa.Abstractions;

namespace Qahwa.Core.Tests;

public class PhoneFieldTests
{
    [Theory]
    [InlineData("+966 50 123 4567")]
    [InlineData("0501234567")]
    [InlineData("(011) 456-7890")]
    [InlineData("+1.202.555.0100")]
    [InlineData("  +44 20 7946 0958  ")]
    public void Real_looking_numbers_are_accepted_and_trimmed(string input) =>
        Assert.Equal(input.Trim(), FieldValueConverter.ToStorage(FieldType.Phone, input));

    [Theory]
    [InlineData("call me")]
    [InlineData("123")]
    [InlineData("+")]
    [InlineData("12345678901234567")]
    [InlineData("050+1234567")]
    [InlineData("=SUM(1,2)")]
    [InlineData("0501234567; DROP TABLE x")]
    [InlineData("<script>1234</script>")]
    public void Anything_else_is_refused(string input) =>
        Assert.Throws<FormatException>(() => FieldValueConverter.ToStorage(FieldType.Phone, input));

    [Fact]
    public void An_empty_phone_is_left_empty() =>
        Assert.Equal(string.Empty, FieldValueConverter.ToStorage(FieldType.Phone, "  "));
}
