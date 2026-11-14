using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Cordyceps.Core;
using Xunit;

namespace Cordyceps.Tests
{
    /// <summary>
    /// Hyperceps is the plugin plus the Claude skill, released under one version number
    /// (<see cref="SkillPairing"/>). These tests check the connection note's opening lines and that the
    /// skill in the repository names the same version the plugin project builds.
    /// </summary>
    public class SkillPairingTests
    {
        [Theory]
        [InlineData("1.0.0+build20261014120000", "1.0.0")]
        [InlineData("1.0.0", "1.0.0")]
        [InlineData(" 1.1.0-rc.1+build1 ", "1.1.0-rc.1")]
        [InlineData(null, BuildVersion.Unknown)]
        public void ReleaseVersion_DropsTheBuildStamp(string version, string expected)
        {
            Assert.Equal(expected, SkillPairing.ReleaseVersion(version));
        }

        [Fact]
        public void Header_NamesTheVersionAndAsksForTheGuide()
        {
            var header = SkillPairing.Header("1.1.0+build20261114120000");
            var lines = header.Split(new[] { Environment.NewLine }, StringSplitOptions.None);

            Assert.StartsWith("Hyperceps 1.1.0: MCP control of Grasshopper", lines[0]);
            Assert.Contains("Built from Cordyceps by Brooks Talley", lines[0]);
            Assert.StartsWith("GUIDE FIRST: before working on the canvas", lines[2]);
            Assert.Contains("the \"hyperceps\" skill, version 1.1.0", lines[2]);
            Assert.Contains("If your app lists a \"hyperceps\" skill, load it", header);
            // Without the installed skill, the plugin serves it: nothing to install.
            Assert.Contains("Otherwise call " + SkillText.Call + ": the plugin returns the same guide", header);
            Assert.Contains("If a loaded skill names another version, call " + SkillText.Call + " instead", header);
            Assert.Contains("once that their installed skill doesn't match the plugin", header);
            Assert.EndsWith(Environment.NewLine + Environment.NewLine, header);
        }

        [Fact]
        public void TheSkillInTheRepository_NamesThePluginsVersion()
        {
            var root = RepositoryRoot();
            var csproj = File.ReadAllText(Path.Combine(root, "src", "Cordyceps", "Cordyceps.csproj"));
            var pluginVersion = Regex.Match(csproj, @"<Version>([^<]+)</Version>").Groups[1].Value.Trim();
            Assert.False(string.IsNullOrEmpty(pluginVersion), "Cordyceps.csproj has no <Version>");

            var skill = File.ReadAllLines(Path.Combine(root, "skills", SkillPairing.SkillName, "SKILL.md"));
            var line = skill.FirstOrDefault(l => l.StartsWith(SkillPairing.SkillVersionLine, StringComparison.Ordinal));
            Assert.True(line != null, $"SKILL.md has no line starting '{SkillPairing.SkillVersionLine}'");

            var skillVersion = line.Substring(SkillPairing.SkillVersionLine.Length).Split(' ')[0].TrimEnd('.');
            Assert.Equal(pluginVersion, skillVersion);
        }

        private static string RepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "skills", SkillPairing.SkillName, "SKILL.md"))
                    && File.Exists(Path.Combine(dir.FullName, "src", "Cordyceps", "Cordyceps.csproj")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Couldn't find the repository root (skills/hyperceps/SKILL.md) above " + AppContext.BaseDirectory);
        }
    }
}
