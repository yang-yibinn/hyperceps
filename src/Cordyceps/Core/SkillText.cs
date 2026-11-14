using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Cordyceps.Core
{
    /// <summary>
    /// The plugin carries its own copy of the Claude skill: skills/hyperceps/SKILL.md is embedded in the
    /// .gha at build time (Cordyceps.csproj, <see cref="ResourceName"/>). An agent whose app has no
    /// installed skill reads the same guide with <c>gh_inspect(action='skill')</c> or the resource
    /// <c>gh://skill</c>. One source file, so the plugin's copy can't differ from the released skill,
    /// and it always matches the plugin's version.
    /// </summary>
    public static class SkillText
    {
        /// <summary>The embedded resource's name (LogicalName in both project files).</summary>
        public const string ResourceName = "Hyperceps.SKILL.md";

        /// <summary>The call the connection note names.</summary>
        public const string Call = "gh_inspect(action='skill')";

        /// <summary>The MCP resource that serves the same file.</summary>
        public const string ResourceUri = "gh://skill";

        /// <summary>The line put before the guide in <see cref="ForAgent"/>.</summary>
        public const string Preface = "The Hyperceps guide (the \"hyperceps\" skill, from the plugin). Read it once: it stays in this conversation.";

        /// <summary>The embedded SKILL.md as it is in the repository, or null when the assembly has none.</summary>
        public static string LoadEmbedded(Assembly assembly)
        {
            if (assembly == null) return null;
            using (var stream = assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null) return null;
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        /// <summary>
        /// The skill without its front matter (the <c>name</c>/<c>description</c> block between the first
        /// two <c>---</c> lines), which is what Claude's Skill tool hands an agent. Text without front
        /// matter comes back unchanged. Windows and Unix line endings both work.
        /// </summary>
        public static string Body(string skillMarkdown)
        {
            if (string.IsNullOrEmpty(skillMarkdown)) return skillMarkdown;
            var text = skillMarkdown.TrimStart('﻿');
            if (!StartsWithFence(text, 0) || !IsFenceLine(text, 0)) return text;

            int pos = LineEnd(text, 0);
            while (pos < text.Length)
            {
                if (StartsWithFence(text, pos) && IsFenceLine(text, pos))
                    return text.Substring(LineEnd(text, pos)).TrimStart('\r', '\n');
                pos = LineEnd(text, pos);
            }
            return text; // no closing fence: not front matter, leave it alone
        }

        /// <summary>
        /// What <c>gh_inspect(action='skill')</c> returns: a one-line preface, then the skill's body.
        /// Null when there is no skill text.
        /// </summary>
        public static string ForAgent(string skillMarkdown)
        {
            var body = Body(skillMarkdown);
            if (string.IsNullOrWhiteSpace(body)) return null;
            return Preface + "\n\n" + body;
        }

        private static bool StartsWithFence(string text, int pos)
            => string.CompareOrdinal(text, pos, "---", 0, 3) == 0;

        private static bool IsFenceLine(string text, int pos)
        {
            int end = text.IndexOf('\n', pos);
            var line = (end < 0 ? text.Substring(pos) : text.Substring(pos, end - pos)).TrimEnd('\r', ' ', '\t');
            return line == "---";
        }

        /// <summary>The index just past the end of the line starting at <paramref name="pos"/>.</summary>
        private static int LineEnd(string text, int pos)
        {
            int end = text.IndexOf('\n', pos);
            return end < 0 ? text.Length : end + 1;
        }
    }
}
