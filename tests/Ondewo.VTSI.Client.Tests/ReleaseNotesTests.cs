#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Ondewo.Vtsi.Client.Tests
{
    /// <summary>
    /// Pins how the GitHub release body is cut out of RELEASE.md. `gh release create -n ""`
    /// succeeds, so a heading whose spelling drifted from what the Makefile greps for, or a section
    /// without its ***** terminator, publishes empty or truncated notes without any error.
    /// </summary>
    public class ReleaseNotesTests
    {
        /// <summary>The product as spelled in the RELEASE.md headings and the Makefile version variable.</summary>
        private const string Product = "VTSI";

        private static readonly string VersionVariable = $"ONDEWO_{Product}_VERSION";

        private static readonly Regex Heading = new(
            $@"^## Release ONDEWO {Product} Csharp Client (?<version>\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant);

        private static readonly Regex Separator = new(@"^\*{5}", RegexOptions.CultureInvariant);

        private static readonly string RepositoryRoot = FindRepositoryRoot();

        private static string[] ReleaseLines => File.ReadAllLines(Path.Combine(RepositoryRoot, "RELEASE.md"));

        private static string Makefile => File.ReadAllText(Path.Combine(RepositoryRoot, "Makefile"));

        [Fact]
        public void TheMakefileSlicesFromTheAnchoredHeadingToTheSeparator()
        {
            // Anchored and quoted (\Q..\E): 9.1.1 must match neither a 9.1.10 heading nor a sentence
            // mentioning the version. A range ending at /\*\*/ would stop at the first **bold** span.
            string expected =
                $"perl -ne 'print if /^\\#+ Release ONDEWO {Product} Csharp Client \\Q${{{VersionVariable}}}\\E$$/../^\\*{{5}}/' RELEASE.md";

            Assert.Contains("RELEASE_NOTES_CMD=" + expected, Makefile, StringComparison.Ordinal);
            Assert.Contains("CURRENT_RELEASE_NOTES=`$(RELEASE_NOTES_CMD)`", Makefile, StringComparison.Ordinal);
            // The notes reach gh on stdin, so nothing in RELEASE.md can change the command line.
            Assert.Contains("$(RELEASE_NOTES_CMD) | GH_TOKEN=", Makefile, StringComparison.Ordinal);
            Assert.Contains("--notes-file -", Makefile, StringComparison.Ordinal);
        }

        [Fact]
        public void TheSliceIsAnchoredOnTheWholeVersion()
        {
            string[] lines =
            {
                "## Release ONDEWO VTSI Csharp Client 9.1.10", "* ten", "*****",
                "## Release ONDEWO VTSI Csharp Client 9.1.1", "* one", "*****",
            };

            List<string> slice = Slice(lines, $@"^#+ Release ONDEWO {Product} Csharp Client {Regex.Escape("9.1.1")}$");

            Assert.Equal(new[] { "## Release ONDEWO VTSI Csharp Client 9.1.1", "* one", "*****" }, slice);
        }

        [Fact]
        public void EveryReleaseHeadingIsSpelledAsTheMakefileExpects()
        {
            List<string> headings = ReleaseLines.Where(line => line.Contains("Release ONDEWO", StringComparison.Ordinal)).ToList();

            Assert.NotEmpty(headings);
            Assert.All(headings, line => Assert.Matches(Heading, line));
            List<string> versions = headings.Select(line => Heading.Match(line).Groups["version"].Value).ToList();
            Assert.Equal(versions.Count, versions.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void EverySectionEndsAtItsSeparator()
        {
            string[] lines = ReleaseLines;
            List<int> headingIndexes = Enumerable.Range(0, lines.Length).Where(i => Heading.IsMatch(lines[i])).ToList();

            Assert.NotEmpty(headingIndexes);
            for (int n = 0; n < headingIndexes.Count; n++)
            {
                int start = headingIndexes[n];
                int end = n + 1 < headingIndexes.Count ? headingIndexes[n + 1] : lines.Length;
                int separator = Array.FindIndex(lines, start + 1, end - start - 1, line => Separator.IsMatch(line));
                Assert.True(separator > start, $"the section '{lines[start]}' has no ***** separator before the next one");
                Assert.True(
                    lines.Skip(start + 1).Take(separator - start - 1).Any(line => line.Trim().Length > 0),
                    $"the section '{lines[start]}' is empty");
            }
        }

        [Fact]
        public void TheCurrentVersionHasNonEmptyReleaseNotes()
        {
            Match version = Regex.Match(Makefile, $@"^{VersionVariable}=(?<version>\S+)\s*$", RegexOptions.Multiline);
            Assert.True(version.Success, $"no {VersionVariable}= line in the Makefile");

            List<string> slice = Slice(ReleaseLines, $@"^#+ Release ONDEWO {Product} Csharp Client {Regex.Escape(version.Groups["version"].Value)}$");

            Assert.True(slice.Count > 2, $"the release notes of {version.Groups["version"].Value} are empty");
            Assert.Matches(Heading, slice[0]);
            Assert.Matches(Separator, slice[^1]);
            Assert.Single(slice, line => Heading.IsMatch(line));
        }

        /// <summary>What <c>perl -ne 'print if /start/../^\*{5}/'</c> prints: start line to separator.</summary>
        private static List<string> Slice(IEnumerable<string> lines, string startPattern)
        {
            var start = new Regex(startPattern, RegexOptions.CultureInvariant);
            var slice = new List<string>();
            bool inside = false;
            foreach (string line in lines)
            {
                if (!inside && start.IsMatch(line))
                {
                    inside = true;
                    slice.Add(line);
                    continue;
                }

                if (inside)
                {
                    slice.Add(line);
                    if (Separator.IsMatch(line))
                    {
                        inside = false;
                    }
                }
            }

            return slice;
        }

        private static string FindRepositoryRoot()
        {
            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "RELEASE.md"))
                    && File.Exists(Path.Combine(directory.FullName, "Makefile")))
                {
                    return directory.FullName;
                }
            }

            throw new InvalidOperationException("no RELEASE.md + Makefile above " + AppContext.BaseDirectory);
        }
    }
}
