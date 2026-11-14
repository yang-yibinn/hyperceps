using System.Collections.Generic;
using Cordyceps.Core;
using Xunit;

namespace Cordyceps.Tests
{
    public class MessageLevelsTests
    {
        [Fact]
        public void Numbers_AreGrasshoppers()
        {
            // Measured in Rhino 8 (Nov 12, 2026) from a script printing (int)GH_RuntimeMessageLevel:
            // Blank 0, Remark 255, Warning 10, Error 20. The plugin casts the enum to these numbers.
            Assert.Equal(0, MessageLevels.Blank);
            Assert.Equal(10, MessageLevels.Warning);
            Assert.Equal(20, MessageLevels.Error);
            Assert.Equal(255, MessageLevels.Remark);
        }

        [Fact]
        public void ARemark_IsNotAProblem_ThoughItsNumberIsTheHighest()
        {
            Assert.True(MessageLevels.IsProblem(MessageLevels.Error));
            Assert.True(MessageLevels.IsProblem(MessageLevels.Warning));
            Assert.False(MessageLevels.IsProblem(MessageLevels.Remark));
            Assert.False(MessageLevels.IsProblem(MessageLevels.Blank));
            Assert.True(MessageLevels.Remark > MessageLevels.Error); // why "level >= Warning" went wrong
        }

        [Fact]
        public void Severity_RanksErrorAboveWarning_AndARemarkAsNothing()
        {
            Assert.Equal(2, MessageLevels.Severity(MessageLevels.Error));
            Assert.Equal(1, MessageLevels.Severity(MessageLevels.Warning));
            Assert.Equal(0, MessageLevels.Severity(MessageLevels.Remark));
            Assert.Equal(0, MessageLevels.Severity(MessageLevels.Blank));
            // A component with only remarks before a sweep that errors at some value has a new problem.
            Assert.True(MessageLevels.Severity(MessageLevels.Remark) < MessageLevels.Severity(MessageLevels.Error));
        }

        [Fact]
        public void Name_SaysWhichLevel()
        {
            Assert.Equal("error", MessageLevels.Name(20));
            Assert.Equal("warning", MessageLevels.Name(10));
            Assert.Equal("remark", MessageLevels.Name(255));
            Assert.Null(MessageLevels.Name(0));
        }

        [Fact]
        public void RemarkNote_FirstRemarkAndHowManyMore()
        {
            Assert.Null(MessageLevels.RemarkNote(new List<string>()));
            Assert.Equal("remark: merged face 2 into face 4", MessageLevels.RemarkNote(new List<string> { "merged face 2 into face 4" }));
            Assert.Equal("remark: radii averaged (+1 more)", MessageLevels.RemarkNote(new List<string> { "radii averaged", "second" }));
            Assert.Equal("remark: line one line two", MessageLevels.RemarkNote(new List<string> { "line one\r\nline two" }));
            var note = MessageLevels.RemarkNote(new List<string> { new string('x', 300) });
            Assert.Equal("remark: ".Length + 100, note.Length);
            Assert.EndsWith("…", note);
        }
    }
}
