using System.Text;
using Onboarding.Core.Configuration;
using Onboarding.Core.LogonScripts;
using Onboarding.Core.Naming;

namespace Onboarding.Tests.Core;

public sealed class LogonScriptGeneratorTests
{
    private static readonly LogonScriptValues Lirion = new("lirion", @"\\dc01\lirion$", "Vertrieb");

    private static LogonScriptTemplate SpecExample() => new()
    {
        DisconnectDrives = ["S", "H", "U", "T", "V"],
        ConnectDrives = [new DriveMapping { Letter = "H", Unc = "{homeUnc}" }],
    };

    [Fact]
    public void Spec_example_is_byte_identical()
    {
        var expected = Encoding.ASCII.GetBytes(
            "net use s: /del /y\r\n" +
            "net use h: /del /y\r\n" +
            "net use u: /del /y\r\n" +
            "net use t: /del /y\r\n" +
            "net use v: /del /y\r\n" +
            "net use h: \\\\dc01\\lirion$\r\n");

        Assert.Equal(expected, LogonScriptGenerator.Generate(SpecExample(), Lirion));
    }

    [Fact]
    public void Uses_crlf_only()
    {
        var bytes = LogonScriptGenerator.Generate(SpecExample(), Lirion);

        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                Assert.True(i > 0 && bytes[i - 1] == (byte)'\r', $"LF without CR at {i}");
            }
        }

        Assert.All(bytes, b => Assert.True(b <= 0x7F));
    }

    [Fact]
    public void Order_is_disconnect_connect_extra_with_placeholders()
    {
        var template = new LogonScriptTemplate
        {
            DisconnectDrives = ["h:"],
            ConnectDrives =
            [
                new DriveMapping { Letter = "h", Unc = "{homeUnc}" },
                new DriveMapping { Letter = "S", Unc = @"\\dc01\{department}" },
            ],
            ExtraLines = ["rem {sam}", "@echo off"],
        };

        var text = LogonScriptGenerator.ToText(LogonScriptGenerator.Generate(template, Lirion));

        Assert.Equal(
            "net use h: /del /y\r\nnet use h: \\\\dc01\\lirion$\r\nnet use s: \\\\dc01\\Vertrieb\r\nrem lirion\r\n@echo off\r\n",
            text);
    }

    [Fact]
    public void Same_input_gives_same_bytes_for_preview_and_file()
    {
        Assert.Equal(
            LogonScriptGenerator.Generate(SpecExample(), Lirion),
            LogonScriptGenerator.Generate(SpecExample(), Lirion));
    }

    [Theory]
    [InlineData(@"\\dc01\Vertrieb-Büro")] // ü > 0x7F
    [InlineData(@"\\dc01\Ablage€")]
    public void Non_ascii_characters_are_rejected(string unc)
    {
        var template = new LogonScriptTemplate { ConnectDrives = [new DriveMapping { Letter = "S", Unc = unc }] };

        Assert.Throws<LogonScriptException>(() => LogonScriptGenerator.Generate(template, Lirion));
    }

    [Fact]
    public void Non_ascii_department_placeholder_is_rejected()
    {
        var template = new LogonScriptTemplate { ExtraLines = ["rem {department}"] };

        Assert.Throws<LogonScriptException>(() =>
            LogonScriptGenerator.Generate(template, Lirion with { Department = "Qualitätssicherung" }));
    }

    [Fact]
    public void Line_breaks_inside_a_line_are_rejected()
    {
        var template = new LogonScriptTemplate { ExtraLines = ["echo a\r\ndel x"] };

        Assert.Throws<LogonScriptException>(() => LogonScriptGenerator.Generate(template, Lirion));
    }

    [Theory]
    [InlineData("")]
    [InlineData("HH")]
    [InlineData("1")]
    [InlineData("Ä")]
    public void Invalid_drive_letter_is_rejected(string letter)
    {
        var template = new LogonScriptTemplate { DisconnectDrives = [letter] };

        Assert.Throws<LogonScriptException>(() => LogonScriptGenerator.Generate(template, Lirion));
    }

    [Fact]
    public void Unknown_placeholder_is_rejected()
    {
        var template = new LogonScriptTemplate { ConnectDrives = [new DriveMapping { Letter = "S", Unc = @"\\dc01\{share}" }] };

        Assert.Throws<TemplateException>(() => LogonScriptGenerator.Generate(template, Lirion));
    }

    [Fact]
    public void Empty_template_gives_empty_file()
    {
        Assert.Empty(LogonScriptGenerator.Generate(new LogonScriptTemplate(), Lirion));
    }
}
