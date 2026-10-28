using Budgeter;

namespace Budgeter.Tests;

public class EnvFileTests
{
    [Fact]
    public void Set_replaces_existing_key_and_keeps_other_lines()
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path, ["# comment", "A=1", "B=2"]);

        EnvFile.Set(path, "A", "new");
        EnvFile.Set(path, "C", "3");

        Assert.Equal(["# comment", "A=new", "B=2", "C=3"], File.ReadAllLines(path));
        Assert.Equal("new", EnvFile.Read(path)["A"]);
        File.Delete(path);
    }

    [Fact]
    public void Read_keeps_equals_signs_in_values()
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path, ["URL=https://u:p@host/x?a=b"]);

        Assert.Equal("https://u:p@host/x?a=b", EnvFile.Read(path)["URL"]);
        File.Delete(path);
    }
}
