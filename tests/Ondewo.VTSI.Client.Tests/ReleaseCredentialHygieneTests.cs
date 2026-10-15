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
    /// Release credentials never reach a process argv. <c>/proc/&lt;pid&gt;/cmdline</c> is world-readable,
    /// so a token on the command line of <c>sh</c>, <c>make</c>, <c>docker</c> or <c>dotnet</c> is
    /// visible to every user of the release machine for the life of that process. make expands
    /// <c>$(NAME)</c> and <c>${NAME}</c> BEFORE it runs <c>sh -c '&lt;line&gt;'</c>, so a recipe may only
    /// read a secret as <c>$${NAME}</c>, which the shell expands from the exported environment.
    /// </summary>
    public class ReleaseCredentialHygieneTests
    {
        /// <summary>Every variable name that carries a credential.</summary>
        private const string Secret = @"[A-Z0-9_]*(?:TOKEN|PASSWORD|API_KEY|SECRET|PASSPHRASE)[A-Z0-9_]*";

        private static readonly string RepositoryRoot = FindRepositoryRoot();

        private static string Makefile => File.ReadAllText(Path.Combine(RepositoryRoot, "Makefile"));

        /// <summary>The Makefile's recipe lines (tab-indented), with their 1-based line numbers.</summary>
        private static IEnumerable<(int Line, string Text)> RecipeLines =>
            Makefile.Split('\n').Select((text, index) => (index + 1, text)).Where(line => line.Item2.StartsWith('\t'));

        [Fact]
        public void TheMakefileDeclaresTheReleaseCredentials()
        {
            // Guards the name pattern itself: if it stopped matching, every test below would pass vacuously.
            Assert.Matches(new Regex($"^GITHUB_GH_TOKEN\\?=", RegexOptions.Multiline), Makefile);
            Assert.Matches(new Regex($"^NUGET_API_KEY\\?=", RegexOptions.Multiline), Makefile);
            Assert.Matches(new Regex($"^{Secret}$"), "NUGET_API_KEY");
            Assert.Matches(new Regex($"^{Secret}$"), "GITHUB_GH_TOKEN");
        }

        [Fact]
        public void MakeNeverExpandsASecretIntoARecipeLine()
        {
            var expanded = new Regex($@"(?<!\$)\$[({{]{Secret}[)}}]");
            Assert.Empty(RecipeLines.Where(line => expanded.IsMatch(line.Text)).Select(line => $"Makefile:{line.Line}: {line.Text.Trim()}"));
        }

        [Fact]
        public void TheDevopsReleaseHandsTheCredentialsOverTheEnvironment()
        {
            string recipe = Recipe("run_release_with_devops");

            Assert.DoesNotContain("$(info)", recipe, StringComparison.Ordinal);
            Assert.Contains("set -a", recipe, StringComparison.Ordinal);
            // Anchored: an unanchored grep also picks up comment lines that merely mention a name.
            Assert.Matches(new Regex(@"grep -h -E '\^\("), recipe);
            Assert.Matches(new Regex(@"\$\(SUBMAKE\) release\s*$"), recipe);
        }

        /// <summary>
        /// The devops-accounts clone holds every ONDEWO release credential. It is cloned OUTSIDE the
        /// repository (which is mounted into the compiler and dotnet containers), deleted right after
        /// the two credentials are read - before the release starts - and deleted by a trap on every
        /// other way out. A directory handed in through DEVOPS_ACCOUNT_DIR belongs to the caller.
        /// </summary>
        [Fact]
        public void TheDevopsCloneIsDeletedBeforeTheReleaseRunsAndOnFailure()
        {
            string recipe = Recipe("run_release_with_devops");
            int clone = recipe.IndexOf("git clone", StringComparison.Ordinal);
            int read = recipe.IndexOf("eval \"$$(grep", StringComparison.Ordinal);
            int delete = recipe.IndexOf("rm -rf \"$$own\" ;", read, StringComparison.Ordinal);
            int release = recipe.IndexOf("$(SUBMAKE) release", StringComparison.Ordinal);

            Assert.True(clone >= 0 && read > clone && delete > read && release > delete,
                "run_release_with_devops must clone, read, delete the clone and only then run $(SUBMAKE) release");
            // GNU make EXECUTES a recipe line referencing $(MAKE) even under -n, so `make -n ondewo_release`
            // would really clone the credentials repository; through SUBMAKE the line is only printed.
            Assert.DoesNotContain("$(MAKE)", recipe, StringComparison.Ordinal);
            Assert.DoesNotContain("${MAKE}", recipe, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"^SUBMAKE:=\$\(MAKE\)\s*$", RegexOptions.Multiline), Makefile);
            Assert.Contains("set -eu", recipe, StringComparison.Ordinal);
            Assert.Contains("mktemp -d", recipe, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"trap 'rm -rf ""\$\$own""' EXIT INT TERM"), recipe);
            Assert.True(recipe.IndexOf("trap ", StringComparison.Ordinal) < clone, "the trap must be armed before the clone");
            Assert.Contains("\"$${DEVOPS_ACCOUNT_DIR:-}\"", recipe, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"^DEVOPS_ACCOUNT_DIR\?=\s*$", RegexOptions.Multiline), Makefile);
            // Cloned into the mktemp directory, never into the (container-mounted) repository.
            Assert.Matches(new Regex(@"git clone [^\n]*\.git ""\$\$own"""), recipe);
            Assert.Single(RecipeLines, line => line.Text.Contains("git clone", StringComparison.Ordinal));
            Assert.Matches(new Regex(@"^ondewo_release: spc run_release_with_devops ##", RegexOptions.Multiline), Makefile);
        }

        /// <summary>
        /// The full release order (RELEASE_CONTRACT): every gate that has no side effect - the read-only
        /// preflight, the build, the whole test suite, the NuGet dry run and the SDK guard - runs before the
        /// first `git add`; the package is published and SEEN on nuget.org before anything reaches
        /// origin; the GitHub release comes last and release_verify checks the result.
        /// </summary>
        [Fact]
        public void TheReleaseRunsInTheContractOrder()
        {
            List<string> steps = RecipeCommands("release");
            string[] order =
            {
                "$(MAKE) release_preflight", "$(MAKE) build", "$(MAKE) test", "$(MAKE) publish_dry_run",
                "$(MAKE) check_nuget_push_sdk", "git add api", "git diff --cached --quiet || git commit --no-verify -m ",
                "$(MAKE) push_to_nuget", "$(MAKE) verify_nuget_published", "git push", "$(MAKE) create_release_branch",
                "$(MAKE) create_release_tag", "$(MAKE) push_to_gh", "$(MAKE) release_verify",
            };

            int previous = -1;
            foreach (string step in order)
            {
                int index = steps.FindIndex(line => step.EndsWith(" ", StringComparison.Ordinal) ? line.StartsWith(step, StringComparison.Ordinal) : line == step);
                Assert.True(index >= 0, $"release has no '{step.Trim()}' step");
                Assert.True(index > previous, $"'{step.Trim()}' runs out of order in release");
                previous = index;
            }

            Assert.Equal("$(MAKE) release_preflight", steps.First(line => !line.StartsWith("echo ", StringComparison.Ordinal)));
            int firstAdd = steps.FindIndex(line => line.StartsWith("git add", StringComparison.Ordinal));
            int firstPush = steps.FindIndex(line => line.StartsWith("git push", StringComparison.Ordinal));
            Assert.True(firstAdd > steps.IndexOf("$(MAKE) check_nuget_push_sdk"), "nothing may be staged before every gate passed");
            Assert.True(firstPush > steps.IndexOf("$(MAKE) verify_nuget_published"), "nothing may reach origin before nuget.org serves the package");
            // `-git commit` ignored a FAILED commit (no identity, say), and the tag then named the previous commit.
            Assert.DoesNotContain(RecipeLines, line => Regex.IsMatch(line.Text, @"^\t-\s*git commit"));
        }

        /// <summary>
        /// The ondewo-vtsi-api driver runs release_preflight and release_verify standalone, with only
        /// DEVOPS_ACCOUNT_DIR set, so both read the credentials from that directory themselves - first.
        /// </summary>
        [Fact]
        public void ThePreflightAndTheVerificationLoadTheirOwnCredentialsFromDevopsAccountDir()
        {
            Assert.Matches(new Regex(@"^LOAD_DEVOPS_CREDENTIALS=if \[ -n ""\$\$\{DEVOPS_ACCOUNT_DIR:-\}"" \] && \[ -d ", RegexOptions.Multiline), Makefile);
            Assert.Contains("eval \"$$(grep -h -E '^(GITHUB_GH_TOKEN|NUGET_API_KEY)=' \\", Makefile, StringComparison.Ordinal);
            foreach (string target in new[] { "release_preflight", "release_verify" })
            {
                Assert.StartsWith("$(LOAD_DEVOPS_CREDENTIALS) ;", RecipeCommands(target)[0], StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// The ondewo-vtsi-api driver and the integration checks pass the polling budget as
        /// VERIFY_ATTEMPTS / VERIFY_INTERVAL_S, the names every ONDEWO client's release_verify takes;
        /// the NuGet poll used to ignore them and always waited its own 30 x 30 s.
        /// </summary>
        [Fact]
        public void TheNugetPollTakesTheSharedVerifyBudgetNames()
        {
            Assert.Matches(new Regex(@"^NUGET_VERIFY_ATTEMPTS\?=\$\(or \$\(VERIFY_ATTEMPTS\),30\)\s*$", RegexOptions.Multiline), Makefile);
            Assert.Matches(new Regex(@"^NUGET_VERIFY_INTERVAL_S\?=\$\(or \$\(VERIFY_INTERVAL_S\),30\)\s*$", RegexOptions.Multiline), Makefile);
            string poll = Recipe("verify_nuget_published");
            Assert.Contains("-ge ${NUGET_VERIFY_ATTEMPTS}", poll, StringComparison.Ordinal);
            Assert.Contains("sleep ${NUGET_VERIFY_INTERVAL_S}", poll, StringComparison.Ordinal);
            Assert.Contains("verify_nuget_published", Recipe("release_verify"), StringComparison.Ordinal);
        }

        [Fact]
        public void ThePreflightAndTheVerificationAreReadOnlyAndCompleteTheirChecks()
        {
            string preflight = Recipe("release_preflight");
            foreach (string check in new[]
                     {
                         "check_release_tools", "check_release_credentials", "check_gh_token_valid",
                         "check_nuget_api_key_valid", "check_version_unreleased", "check_release_notes",
                     })
            {
                Assert.Contains(check, preflight, StringComparison.Ordinal);
            }

            Assert.Contains("git ls-remote --exit-code origin", Recipe("check_version_unreleased"), StringComparison.Ordinal);
            Assert.Contains("${NUGET_FLATCONTAINER}/index.json", Recipe("check_version_unreleased"), StringComparison.Ordinal);
            Assert.Contains("--jq .permissions.push", Recipe("check_gh_token_valid"), StringComparison.Ordinal);
            string nugetKey = Recipe("check_nuget_api_key_valid");
            Assert.Contains("create-verification-key", Makefile, StringComparison.Ordinal);
            Assert.Contains("| curl -sS --connect-timeout 20 --max-time 60 -K - ", nugetKey, StringComparison.Ordinal);
            Assert.Contains("-o /dev/null", nugetKey, StringComparison.Ordinal);
            Assert.Contains("verify_tag_on_origin verify_gh_release verify_nuget_published", Recipe("release_verify"), StringComparison.Ordinal);
            Assert.Contains("--json body --jq .body", Recipe("verify_gh_release"), StringComparison.Ordinal);
            foreach (string target in new[] { "release_preflight", "release_verify", "check_version_unreleased", "verify_nuget_published", "verify_gh_release" })
            {
                string recipe = Recipe(target);
                Assert.DoesNotMatch(new Regex(@"\bgit (?:push|tag|commit)\b|nuget push|release create"), recipe);
            }
        }

        /// <summary>
        /// The credentials are taken out of the Makefile's blanket `export` and handed back only to the
        /// targets that use them; `dotnet restore/build/test/pack` - which run third-party package build
        /// targets - never see one. The dotnet wrapper forwards NUGET_API_KEY to `nuget push` alone.
        /// </summary>
        [Fact]
        public void CredentialsReachOnlyTheStepsThatUseThem()
        {
            string[] lines = Makefile.Split('\n');
            int unexport = Array.FindIndex(lines, line => Regex.IsMatch(line, @"^unexport GITHUB_GH_TOKEN NUGET_API_KEY GH_TOKEN GITHUB_TOKEN\s*$"));
            Assert.True(unexport > Array.FindIndex(lines, line => line.StartsWith("NUGET_API_KEY?=", StringComparison.Ordinal)),
                "the unexport must follow the ?= defaults, or it defines the variables as empty first");
            Assert.True(unexport > Array.FindIndex(lines, line => line.StartsWith("GITHUB_GH_TOKEN?=", StringComparison.Ordinal)));
            Assert.Contains("$(GITHUB_CREDENTIAL_TARGETS): export GITHUB_GH_TOKEN:=$(GITHUB_GH_TOKEN)", Makefile, StringComparison.Ordinal);
            Assert.Contains("$(NUGET_CREDENTIAL_TARGETS): export NUGET_API_KEY:=$(NUGET_API_KEY)", Makefile, StringComparison.Ordinal);

            string[] nugetTargets = VariableWords("NUGET_CREDENTIAL_TARGETS");
            string[] githubTargets = VariableWords("GITHUB_CREDENTIAL_TARGETS");
            Assert.Contains("push_to_nuget", nugetTargets);
            Assert.Contains("build_gh_release", githubTargets);
            foreach (string toolchainTarget in new[] { "build", "build_library", "test", "pack", "publish_dry_run", "verify_nupkg_installs", "generate_ondewo_protos" })
            {
                Assert.DoesNotContain(toolchainTarget, nugetTargets);
                Assert.DoesNotContain(toolchainTarget, githubTargets);
            }

            string[] wrapper = File.ReadAllLines(Path.Combine(RepositoryRoot, "tools", "dotnet-in-docker", "dotnet"))
                .Where(line => !line.TrimStart().StartsWith("#", StringComparison.Ordinal)).ToArray();
            int[] keyLines = Enumerable.Range(0, wrapper.Length).Where(i => wrapper[i].Contains("NUGET_API_KEY", StringComparison.Ordinal)).ToArray();
            Assert.Single(keyLines);
            Assert.Contains("set -- -e NUGET_API_KEY", wrapper[keyLines[0]], StringComparison.Ordinal);
            Assert.Equal("if [ \"${1:-}\" = nuget ] && [ \"${2:-}\" = push ]; then", wrapper[keyLines[0] - 1].Trim());
        }

        /// <summary>
        /// gh never logs in: every call gets GH_TOKEN for itself, from GITHUB_GH_TOKEN, so no token is
        /// persisted in a gh config on the release host and a stale GH_TOKEN / GITHUB_TOKEN in the caller's
        /// environment is never the one used. Nothing needs sudo, and nothing needs unzip.
        /// </summary>
        [Fact]
        public void GhGetsItsTokenPerCallAndTheReleaseNeedsNoSudoUnzipOrLogin()
        {
            List<(int Line, string Text)> gh = RecipeLines.Where(line => line.Text.Contains("$(GH)", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(gh);
            // ...and no recipe runs a bare `gh`, which would use whatever login or token the host has.
            Assert.DoesNotContain(RecipeLines, line => Regex.IsMatch(line.Text, @"(?:^\t[@-]*|[;&|(]\s*)gh\s"));
            Assert.All(gh, line => Assert.Contains("GH_TOKEN=\"$$GITHUB_GH_TOKEN\" $(GH) ", line.Text, StringComparison.Ordinal));
            Assert.DoesNotContain(RecipeLines, line => line.Text.Contains("auth login", StringComparison.Ordinal));
            Assert.DoesNotContain(RecipeLines, line => Regex.IsMatch(line.Text, @"\b(?:sudo|unzip)\b"));
            // "already exists" on nuget.org is a failure, never a silent no-op - here and in release.yml.
            Assert.DoesNotContain(RecipeLines, line => line.Text.Contains("--skip-duplicate", StringComparison.Ordinal));
            Assert.All(Workflows(), workflow => Assert.DoesNotContain(
                RunScriptLines(File.ReadAllLines(workflow)),
                line => line.Text.Contains("--skip-duplicate", StringComparison.Ordinal)));
        }

        /// <summary>Every image the release runs is pinned by exact version AND digest, and the SDK pin exists once.</summary>
        [Fact]
        public void TheReleaseImagesArePinnedByDigest()
        {
            string sdkTag = Regex.Match(Makefile, @"^DOTNET_SDK_TAG=(\S+)\s*$", RegexOptions.Multiline).Groups[1].Value;
            Assert.Matches(new Regex(@"^\d+\.\d+\.\d+-[a-z]+@sha256:[0-9a-f]{64}$"), sdkTag);
            Assert.Contains("DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_TAG}", Makefile, StringComparison.Ordinal);
            Assert.Contains("--build-arg DOTNET_SDK_VERSION=${DOTNET_SDK_TAG}", Recipe("build_compiler"), StringComparison.Ordinal);
            Assert.Contains(
                $"image=\"${{DOTNET_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:{sdkTag}}}\"",
                File.ReadAllText(Path.Combine(RepositoryRoot, "tools", "dotnet-in-docker", "dotnet")),
                StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"^PROTO_COMPILER_IMAGE=.*:latest", RegexOptions.Multiline), Makefile);

            string ghDockerfile = File.ReadAllText(Path.Combine(RepositoryRoot, "tools", "gh-in-docker", "Dockerfile"));
            Assert.Matches(new Regex(@"^FROM [\w./-]+:\d+\.\d+\.\d+@sha256:[0-9a-f]{64}$", RegexOptions.Multiline), ghDockerfile);
            Assert.Matches(new Regex(@"^ARG GH_VERSION=\d+\.\d+\.\d+$", RegexOptions.Multiline), ghDockerfile);
            Assert.Contains("sha256sum -c -", ghDockerfile, StringComparison.Ordinal);
        }

        [Fact]
        public void NoMakeInvocationGetsACredentialAsAnArgument()
        {
            var assignment = new Regex($@"(?:\$\(MAKE\)|\bmake)\s[^\n]*\b{Secret}=");
            Assert.Empty(RecipeLines.Where(line => assignment.IsMatch(line.Text)).Select(line => $"Makefile:{line.Line}: {line.Text.Trim()}"));
        }

        [Fact]
        public void NoDockerRunGetsACredentialValue()
        {
            // `-e NAME` makes docker copy the value from its own environment; `-e NAME=value` is argv.
            var valued = new Regex($@"(?:-e|--env)[ =]+{Secret}=");
            foreach (string file in ScannedFiles())
            {
                Assert.DoesNotMatch(valued, File.ReadAllText(file));
            }
        }

        [Fact]
        public void NoToolGetsACredentialFlagOnItsArgv()
        {
            var flag = new Regex(
                $@"(?:--api-key|--symbol-api-key|-k|-sk|--token|--password|-p)[ =]+[""']?\$+[({{]?{Secret}|Authorization:[^\n]*{Secret}");
            foreach (string file in ScannedFiles())
            {
                Assert.DoesNotMatch(flag, File.ReadAllText(file));
            }
        }

        [Fact]
        public void DotnetNugetPushReadsTheKeyFromTheEnvironment()
        {
            string recipe = Recipe("push_to_nuget");

            Assert.Contains("dotnet nuget push \"${NUPKG}\"", recipe, StringComparison.Ordinal);
            Assert.DoesNotContain("--api-key", recipe, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"\s-k\s"), recipe);
            // An SDK older than 10.0.400 ignores NUGET_API_KEY and would push without any key.
            Assert.Contains("dotnet nuget push --help 2>/dev/null | grep -q NUGET_API_KEY", recipe, StringComparison.Ordinal);
        }

        [Fact]
        public void NoWorkflowInterpolatesASecretIntoARunScript()
        {
            // `${{ secrets.X }}` inside run: is substituted into the script text, i.e. bash's argv and
            // the step log; it belongs in env: and the script reads $X.
            foreach (string workflow in Workflows())
            {
                Assert.Empty(RunScriptLines(File.ReadAllLines(workflow))
                    .Where(line => line.Text.Contains("${{ secrets.", StringComparison.Ordinal))
                    .Select(line => $"{Path.GetFileName(workflow)}:{line.Line}: {line.Text.Trim()}"));
            }
        }

        [Fact]
        public void TheWorkflowScanSeesRunScripts()
        {
            // Guards RunScriptLines: a block scalar and a one-line run: are both recognised, with: is not.
            string[] sample =
            {
                "    steps:",
                "      - name: a",
                "        run: echo ${{ secrets.ONE }}",
                "      - name: b",
                "        with:",
                "          token: ${{ secrets.TWO }}",
                "      - run: |",
                "          set -eu",
                "          curl ${{ secrets.THREE }}",
                "        env:",
                "          X: ${{ secrets.FOUR }}",
            };
            List<string> hits = RunScriptLines(sample).Where(l => l.Text.Contains("secrets.", StringComparison.Ordinal)).Select(l => l.Text.Trim()).ToList();

            Assert.Equal(new[] { "run: echo ${{ secrets.ONE }}", "curl ${{ secrets.THREE }}" }, hits);
        }

        /// <summary>The lines of every <c>run:</c> script (one-line or block scalar), 1-based.</summary>
        private static IEnumerable<(int Line, string Text)> RunScriptLines(string[] lines)
        {
            var run = new Regex(@"^(?<indent>\s*)(?:-\s+)?run:\s*(?<value>.*)$");
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = run.Match(lines[i]);
                if (!match.Success)
                {
                    continue;
                }

                yield return (i + 1, lines[i]);
                int keyIndent = lines[i].IndexOf("run:", StringComparison.Ordinal);
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string next = lines[j];
                    if (next.Trim().Length > 0 && next.Length - next.TrimStart().Length <= keyIndent)
                    {
                        break;
                    }

                    yield return (j + 1, next);
                }
            }
        }

        /// <summary>The shell commands of <paramref name="target"/>'s recipe, without the @ / - prefixes and make comments.</summary>
        private static List<string> RecipeCommands(string target) =>
            Recipe(target).Split('\n').Skip(1)
                .Where(line => line.StartsWith('\t'))
                .Select(line => line.Trim().TrimStart('@', '-').Trim())
                .ToList();

        /// <summary>The whitespace-separated words of a (possibly continued) Makefile variable.</summary>
        private static string[] VariableWords(string name)
        {
            Match match = Regex.Match(Makefile, $@"^{name}=((?:[^\n]*\\\n)*[^\n]*)", RegexOptions.Multiline);
            Assert.True(match.Success, $"no {name}= in the Makefile");
            return match.Groups[1].Value.Replace("\\", " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>The recipe of <paramref name="target"/>: its rule line up to the next blank line.</summary>
        private static string Recipe(string target)
        {
            Match match = Regex.Match(Makefile, $@"^{Regex.Escape(target)}:.*?(?=\n\n|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
            Assert.True(match.Success, $"no {target} target in the Makefile");
            return match.Value;
        }

        private static IEnumerable<string> Workflows()
        {
            string directory = Path.Combine(RepositoryRoot, ".github", "workflows");
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.yml").Concat(Directory.EnumerateFiles(directory, "*.yaml"))
                : Enumerable.Empty<string>();
        }

        private static IEnumerable<string> ScannedFiles() =>
            new[] { Path.Combine(RepositoryRoot, "Makefile") }
                .Concat(Directory.EnumerateFiles(RepositoryRoot, "Dockerfile*"))
                .Concat(Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "tools"), "*", SearchOption.AllDirectories))
                .Concat(Workflows());

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
