using System;
using Cordyceps.Core;

namespace Cordyceps.Tools.Unified
{
    /// <summary>
    /// Hyperceps: <c>gh_inspect(action='skill')</c> returns the plugin's own copy of the Claude skill
    /// (skills/hyperceps/SKILL.md, embedded at build time; <see cref="SkillText"/>), so an app without
    /// the installed skill still gets the guide, at the plugin's own version.
    /// </summary>
    public partial class GhInspectTool
    {
        private static readonly Lazy<string> _skill =
            new Lazy<string>(() => SkillText.ForAgent(SkillText.LoadEmbedded(typeof(GhInspectTool).Assembly)));

        /// <summary>
        /// Plain text like <c>action='help'</c>, so the guide reads as markdown rather than escaped JSON.
        /// Like the connection probe it never touches the document or the UI thread, so it answers while
        /// Grasshopper is busy.
        /// </summary>
        private string ActionSkill()
            => _skill.Value ?? ToolHelpers.ErrorResponse(
                "This build of Hyperceps carries no copy of the skill. Get hyperceps-skill.zip from "
                + SkillPairing.ReleasesUrl + " instead.");
    }
}
