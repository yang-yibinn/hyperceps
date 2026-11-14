using System;
using Cordyceps.Core;
using Xunit;

namespace Cordyceps.Tests
{
    /// <summary>
    /// The connection note must fit in what Claude Code shows (<see cref="ServerNote.ClaudeCodeLimit"/>):
    /// the 1.0.0 note was cut in half there. These tests keep it under <see cref="ServerNote.MaxLength"/>
    /// and check that the parts agents need come first.
    /// </summary>
    public class ServerNoteTests
    {
        [Theory]
        [InlineData("1.1.0+build20261114120000")]
        [InlineData("10.10.10-rc.10+build1")]
        public void TheNote_FitsWhatClaudeCodeShows_EvenWithWindowsLineEndings(string version)
        {
            var note = ServerNote.Text(version).Replace("\r\n", "\n").Replace("\n", "\r\n");
            Assert.True(note.Length <= ServerNote.MaxLength,
                $"The connection note is {note.Length} characters; keep it at most {ServerNote.MaxLength} (Claude Code shows {ServerNote.ClaudeCodeLimit}).");
            Assert.True(ServerNote.MaxLength < ServerNote.ClaudeCodeLimit);
        }

        [Fact]
        public void TheNote_OpensWithTheVersionThenTheGuide()
        {
            var note = ServerNote.Text("1.1.0");
            Assert.StartsWith(SkillPairing.Header("1.1.0"), note);
            Assert.True(note.IndexOf(SkillText.Call, StringComparison.Ordinal) is int i && i > 0 && i < 600,
                "the note should name gh_inspect(action='skill') in its first lines");
        }

        [Theory]
        [InlineData("WORKING METHOD")]
        [InlineData("8. To check results across slider")]
        [InlineData("NAMING: never rename native components")]
        [InlineData("LICHEN THALLI")]
        [InlineData("never the whole script again")]
        [InlineData("gh_inspect(action='connection')")]
        [InlineData("modal_inferred=true")]
        [InlineData("never suggest F5")]
        [InlineData("gh://skill")]
        public void TheNote_KeepsWhatAgentsNeed(string part)
        {
            Assert.Contains(part, ServerNote.Text("1.1.0"));
        }
    }
}
