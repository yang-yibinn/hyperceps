using System;
using System.IO;
using Cordyceps.Core;
using Xunit;

namespace Cordyceps.Tests
{
    /// <summary>
    /// The plugin's own copy of the skill (<see cref="SkillText"/>): it is the repository's SKILL.md,
    /// embedded at build time, and gh_inspect(action='skill') hands it out without the front matter,
    /// as Claude's Skill tool does.
    /// </summary>
    public class SkillTextTests
    {
        [Fact]
        public void TheEmbeddedCopy_IsTheRepositorysSkill()
        {
            var embedded = SkillText.LoadEmbedded(typeof(SkillText).Assembly);
            Assert.NotNull(embedded);

            var file = File.ReadAllText(Path.Combine(RepositoryRoot(), "skills", SkillPairing.SkillName, "SKILL.md"));
            Assert.Equal(Normalize(file), Normalize(embedded));
        }

        [Fact]
        public void ForAgent_IsThePrefaceThenTheSkillWithoutItsFrontMatter()
        {
            var embedded = SkillText.LoadEmbedded(typeof(SkillText).Assembly);
            var text = SkillText.ForAgent(embedded);

            Assert.StartsWith(SkillText.Preface + "\n\n# Working with Grasshopper through Hyperceps", Normalize(text));
            Assert.DoesNotContain("description: The companion skill", text);
            Assert.Contains(SkillPairing.SkillVersionLine, text);
            // The whole body, to its last line.
            var lastLine = Normalize(embedded).TrimEnd('\n');
            lastLine = lastLine.Substring(lastLine.LastIndexOf('\n') + 1);
            Assert.EndsWith(lastLine, Normalize(text).TrimEnd('\n'));
        }

        [Theory]
        [InlineData("---\nname: x\ndescription: y\n---\n\n# Title\nbody\n", "# Title\nbody\n")]
        [InlineData("---\r\nname: x\r\n---\r\n# Title\r\nbody", "# Title\r\nbody")]
        [InlineData("﻿---\nname: x\n---\n# Title", "# Title")]
        [InlineData("---  \nname: x\n---\t\n# Title", "# Title")]
        public void Body_DropsTheFrontMatter(string skill, string expected)
        {
            Assert.Equal(expected, SkillText.Body(skill));
        }

        [Theory]
        [InlineData("# Title\n---\nbody")]           // no front matter
        [InlineData("---\nname: x\n# never closed")] // no closing fence
        [InlineData("----\nname: x\n----\n# Title")] // not a fence
        public void Body_LeavesOtherTextAlone(string skill)
        {
            Assert.Equal(skill, SkillText.Body(skill));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("---\nname: x\n---\n   \n")]
        public void ForAgent_IsNullWithoutText(string skill)
        {
            Assert.Null(SkillText.ForAgent(skill));
        }

        [Fact]
        public void LoadEmbedded_IsNullWhenTheAssemblyHasNoSkill()
        {
            Assert.Null(SkillText.LoadEmbedded(typeof(Xunit.Assert).Assembly));
            Assert.Null(SkillText.LoadEmbedded(null));
        }

        private static string Normalize(string s) => s.TrimStart('﻿').Replace("\r\n", "\n");

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
            throw new InvalidOperationException("Couldn't find the repository root above " + AppContext.BaseDirectory);
        }
    }
}
