using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using RD.Web.Components.Layout;

namespace RD.Tests;

public sealed class MainLayoutSpacingTests
{
    [Fact]
    public async Task PagePadding_DoesNotOverride_MainContentAppBarClearance()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.AddAuthorization();

        var layout = context.Render<MainLayout>(parameters => parameters
            .Add(component => component.Body, (RenderFragment)(builder =>
                builder.AddMarkupContent(0, "<div id=\"page-probe\">Page</div>"))));

        var main = layout.Find(".mud-main-content");

        main.ClassList.Should().NotContain("pa-4");
        main.ClassList.Should().NotContain("pa-md-6");
        main.QuerySelector(".rd-page-content")!.ClassList.Should().Contain(["pa-4", "pa-md-6"]);
    }
}
