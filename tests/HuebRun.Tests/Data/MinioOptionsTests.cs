using System.ComponentModel.DataAnnotations;
using HuebRun.Infrastructure.Payments;

namespace HuebRun.Tests.Data;

public class MinioOptionsTests
{
    private static MinioOptions Valid() => new()
    {
        Endpoint = "localhost:9000", AccessKey = "a", SecretKey = "s", Bucket = "b", AllowedExtensions = [".png"],
    };

    private static bool IsValid(MinioOptions o) => Validator.TryValidateObject(o, new ValidationContext(o), [], validateAllProperties: true);

    [Fact]
    public void Valid_options_pass() => Assert.True(IsValid(Valid()));

    [Fact]
    public void Empty_allowed_extensions_fail_validation_at_startup()
    {
        var o = Valid();
        o.AllowedExtensions = [];
        Assert.False(IsValid(o));
    }
}
