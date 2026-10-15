#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ondewo.Vtsi.Client.Tests
{
    /// <summary>
    /// The services and RPCs of the <c>ondewo.vtsi</c> package, DERIVED rather than written down, so the
    /// API-surface tests follow the next API release instead of breaking on it (a hard-coded
    /// <c>Assert.Equal(48, ...)</c> did exactly that).
    /// <para>
    /// The source is the protos of the pinned <c>ondewo-vtsi-api</c> submodule when it is checked out
    /// (CI checks it out), otherwise the committed stubs under <c>api/Ondewo/Vtsi/*Grpc.cs</c> - their
    /// text, which is independent of the compiled descriptors the tests compare it against.
    /// </para>
    /// </summary>
    internal static class VtsiApiSurface
    {
        internal const string Package = "ondewo.vtsi";

        private static readonly Lazy<(string Source, IReadOnlyDictionary<string, IReadOnlyList<Rpc>> Services)> Loaded =
            new(Load);

        /// <summary>Where the surface was read from: <c>protos</c> or <c>stubs</c>.</summary>
        internal static string Source => Loaded.Value.Source;

        /// <summary>Full service name -> its RPCs in declaration order.</summary>
        internal static IReadOnlyDictionary<string, IReadOnlyList<Rpc>> Services => Loaded.Value.Services;

        internal static string ProtosDirectory => Path.Combine(RepositoryRoot, "ondewo-vtsi-api", "ondewo", "vtsi");

        internal static string RepositoryRoot { get; } = FindRepositoryRoot();

        internal static IReadOnlyList<Rpc> RpcsOf(string service)
        {
            Assert(Services.TryGetValue(service, out IReadOnlyList<Rpc>? rpcs), $"{service} is not declared by the {Source}");
            return rpcs!;
        }

        internal static string[] RpcNamesOf(string service) => RpcsOf(service).Select(rpc => rpc.Name).ToArray();

        /// <summary>Parses <c>service</c> / <c>rpc</c> declarations out of proto source text.</summary>
        internal static Dictionary<string, List<Rpc>> ParseProto(string text)
        {
            // Comments out, string literals kept: an option string may contain "//".
            string code = Regex.Replace(
                text,
                @"""(?:\\.|[^""\\])*""|//[^\n]*|/\*.*?\*/",
                match => match.Value.StartsWith("\"", StringComparison.Ordinal) ? match.Value : " ",
                RegexOptions.Singleline);
            string package = Regex.Match(code, @"\bpackage\s+([\w.]+)\s*;").Groups[1].Value;
            var services = new Dictionary<string, List<Rpc>>(StringComparer.Ordinal);
            foreach (Match service in Regex.Matches(code, @"\bservice\s+(\w+)\s*\{"))
            {
                string body = BlockAt(code, service.Index + service.Length - 1);
                var rpcs = new List<Rpc>();
                foreach (Match rpc in Regex.Matches(
                             body,
                             @"\brpc\s+(\w+)\s*\(\s*(stream\s+)?[\w.]+\s*\)\s*returns\s*\(\s*(stream\s+)?[\w.]+\s*\)"))
                {
                    rpcs.Add(new Rpc(rpc.Groups[1].Value, rpc.Groups[2].Success, rpc.Groups[3].Success));
                }

                services[$"{package}.{service.Groups[1].Value}"] = rpcs;
            }

            return services;
        }

        /// <summary>Parses the method table protoc's grpc_csharp_plugin writes into a <c>*Grpc.cs</c> file.</summary>
        internal static Dictionary<string, List<Rpc>> ParseStub(string text)
        {
            var services = new Dictionary<string, List<Rpc>>(StringComparer.Ordinal);
            string? name = null;
            foreach (Match match in Regex.Matches(
                         text,
                         @"__ServiceName\s*=\s*""(?<service>[\w.]+)""|grpc::MethodType\.(?<type>\w+),\s*__ServiceName,\s*""(?<rpc>\w+)"""))
            {
                if (match.Groups["service"].Success)
                {
                    name = match.Groups["service"].Value;
                    services[name] = new List<Rpc>();
                    continue;
                }

                Assert(name != null, "a method table before its __ServiceName");
                string type = match.Groups["type"].Value;
                services[name!].Add(new Rpc(
                    match.Groups["rpc"].Value,
                    type is "ClientStreaming" or "DuplexStreaming",
                    type is "ServerStreaming" or "DuplexStreaming"));
            }

            return services;
        }

        private static (string, IReadOnlyDictionary<string, IReadOnlyList<Rpc>>) Load()
        {
            bool fromProtos = Directory.Exists(ProtosDirectory)
                              && Directory.EnumerateFiles(ProtosDirectory, "*.proto").Any();
            IEnumerable<string> files = fromProtos
                ? Directory.EnumerateFiles(ProtosDirectory, "*.proto")
                : Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "api", "Ondewo", "Vtsi"), "*Grpc.cs");

            var services = new SortedDictionary<string, IReadOnlyList<Rpc>>(StringComparer.Ordinal);
            foreach (string file in files.OrderBy(file => file, StringComparer.Ordinal))
            {
                string text = File.ReadAllText(file);
                foreach ((string service, List<Rpc> rpcs) in fromProtos ? ParseProto(text) : ParseStub(text))
                {
                    if (service.StartsWith(Package + ".", StringComparison.Ordinal))
                    {
                        services[service] = rpcs;
                    }
                }
            }

            Assert(services.Count > 0, $"no {Package} service found in {(fromProtos ? ProtosDirectory : "api/Ondewo/Vtsi")}");
            return (fromProtos ? "protos" : "stubs", services);
        }

        /// <summary>The text between the brace at <paramref name="open"/> and its matching close.</summary>
        private static string BlockAt(string code, int open)
        {
            int depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{')
                {
                    depth++;
                }
                else if (code[i] == '}' && --depth == 0)
                {
                    return code.Substring(open + 1, i - open - 1);
                }
            }

            throw new InvalidOperationException("unbalanced braces in a proto file");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
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

        internal sealed record Rpc(string Name, bool ClientStreaming, bool ServerStreaming);
    }
}
