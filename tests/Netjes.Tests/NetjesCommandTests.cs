using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Netjes.Tests;

public class NetjesCommandTests
{
    [Fact]
    public async Task GetNetjesCommand_NamesTheCommandTheCliRuns_OrIsNullForAnOrdinaryRun()
    {
        var command = WebApplication.CreateBuilder();
        command.Services.AddNetjes(o => o.Command = new NetjesCommandOptions { Name = "generate" });
        await using var generating = command.Build();
        var ordinary = WebApplication.CreateBuilder();
        ordinary.Services.AddNetjes();
        await using var serving = ordinary.Build();

        Assert.Equal("generate", generating.GetNetjesCommand());
        Assert.Null(serving.GetNetjesCommand());
    }
}
