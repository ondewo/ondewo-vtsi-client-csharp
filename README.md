<div align="center">
  <table>
    <tr>
      <td>
        <a href="https://ondewo.com">
            <img width="400px" src="https://raw.githubusercontent.com/ondewo/ondewo-logos/master/ondewo_we_automate_your_phone_calls.png"/>
        </a>
      </td>
    </tr>
    <tr>
       <td align="center">
          <a href="https://www.linkedin.com/company/ondewo "><img width="40px" src="https://cdn-icons-png.flaticon.com/512/3536/3536505.png"></a>
          <a href="https://www.facebook.com/ondewo"><img width="40px" src="https://cdn-icons-png.flaticon.com/512/733/733547.png"></a>
          <a href="https://twitter.com/ondewo"><img width="40px" src="https://cdn-icons-png.flaticon.com/512/733/733579.png"> </a>
          <a href="https://www.instagram.com/ondewo.ai/"><img width="40px" src="https://cdn-icons-png.flaticon.com/512/174/174855.png"></a>
       </td>
    </tr>
  </table>
  <h1 align="center">
    ONDEWO VTSI Client C-Sharp
  </h1>
</div>

## Overview

`Ondewo.VTSI.Client` is a compiled version of the
[ONDEWO VTSI API](https://github.com/ondewo/ondewo-vtsi-api) — the gRPC interface to ONDEWO
Virtual Telephony Server Interface — generated with the
[ONDEWO PROTO COMPILER](https://github.com/ondewo/ondewo-proto-compiler).

ONDEWO APIs use [Protocol Buffers](https://github.com/protocolbuffers/protobuf) version 3 (proto3) as their
Interface Definition Language (IDL) to define the API interface and the structure of the payload messages. The same
interface definition is used for the gRPC versions of the API in all languages.

Nothing in `api/` is written by hand: it is the output of the `ondewo-csharp-proto-compiler` docker image running
over the `ondewo-vtsi-api` submodule, and it is regenerated in full by `make build`.

## Installation

The package is published to [nuget.org](https://www.nuget.org/packages/Ondewo.VTSI.Client) as
**`Ondewo.VTSI.Client`**. No custom feed, credential or `nuget.config` entry is needed — the default
`nuget.org` source is enough.

With the .NET CLI, from the directory of the project that should consume it:

```shell
dotnet add package Ondewo.VTSI.Client
```

That resolves the latest stable version. To pin one — which is what you want in a service, because
the client version tracks the ONDEWO VTSI API in major and minor:

```shell
dotnet add package Ondewo.VTSI.Client --version 9.0.0
```

Or write the `PackageReference` item into your `.csproj` directly:

```xml
<ItemGroup>
  <PackageReference Include="Ondewo.VTSI.Client" Version="9.0.0" />
</ItemGroup>
```

In the Visual Studio Package Manager Console:

```powershell
Install-Package Ondewo.VTSI.Client -Version 9.0.0
```

A few things worth knowing before you take the dependency:

- **Target framework.** The package is built for `netstandard2.0` and `net10.0`. The `netstandard2.0` build is consumable from .NET
  Framework 4.6.1+, .NET Core 2.0+ and every modern .NET. Note that gRPC over
  `Grpc.Net.Client` needs HTTP/2, which in practice means .NET Core 3.0+ / .NET 5+; on .NET
  Framework you additionally need `Grpc.Net.Client.Web` or the legacy `Grpc.Core` channel.
- **Transitive dependencies.** `Google.Protobuf`, `Grpc.Net.Client`, `Grpc.Core.Api` and
  `Google.Api.CommonProtos` come with it, at the versions pinned by the compiler image. The
  `google/api`, `google/rpc` and `google/type` descriptors the ONDEWO API imports are taken from
  `Google.Api.CommonProtos` rather than generated a second time into this assembly, so they never
  collide with another Google library in your graph.
- **What else is in the assembly.** `ondewo-vtsi-api` imports the NLU, QA, S2T, SIP and T2S protos
  directly, so the package carries the `Ondewo.Nlu`, `Ondewo.Qa`, `Ondewo.S2T`, `Ondewo.Sip`,
  `Ondewo.T2S` and `Google.Cloud.Dialogflow.V2` stubs alongside `Ondewo.Vtsi` — it is
  self-contained and needs no sibling ONDEWO package. For the same reason do **not** reference
  `Ondewo.NLU.Client`, `Ondewo.S2T.Client`, `Ondewo.SIP.Client` or `Ondewo.T2S.Client` next to it:
  both assemblies would define the same types and every use of one becomes CS0433 ("the type
  exists in both").
- **Debugging.** Every release also publishes a `.snupkg` symbol package to the nuget.org symbol
  server, so stepping into the generated stubs works once `https://symbols.nuget.org/download/symbols`
  is enabled in your debugger's symbol settings.
- **Versioning.** `Ondewo.VTSI.Client` **9.0.x** is generated from ONDEWO VTSI API **9.0.0**: major
  and minor always match the API, the patch number is this client's own.

From source:

```shell
git clone https://github.com/ondewo/ondewo-vtsi-client-csharp.git   ## Clone the repository
cd ondewo-vtsi-client-csharp                                        ## Change into the repo directory
make setup_developer_environment_locally              ## Check out the submodules, install the hooks
make build                                            ## Regenerate the stubs and pack the NuGet package
```

The package targets `netstandard2.0` and `net10.0`: the `netstandard2.0` build is consumable from .NET Framework
4.6.1+, .NET Core 2.0+ and every modern .NET, and only the `net10.0` build carries the TLS / mutual-TLS channel
factory (see [TLS, mutual TLS and certificates](#tls-mutual-tls-and-certificates)). It brings `Google.Protobuf`, `Grpc.Net.Client`, `Grpc.Core.Api` and `Google.Api.CommonProtos` with
it — the versions are pinned by the compiler image, never by hand.

## Usage

```csharp
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;

// protoc derives the C# namespace from the proto `package` declaration and PascalCases it, so
// `package ondewo.vtsi;` becomes `Ondewo.<Pascal>` — check the `namespace` line at the top of
// any generated file under api/ for the exact spelling.
using Ondewo.Vtsi;

// A bearer token is attached to every call through CallCredentials, so it is refreshed in one
// place instead of being copied into each request's Metadata.
var callCredentials = CallCredentials.FromInterceptor((context, metadata) =>
{
    metadata.Add("authorization", $"Bearer {accessToken}");
    return Task.CompletedTask;
});

using var channel = GrpcChannel.ForAddress(
    "https://grpc-vtsi.ondewo.com:443",
    new GrpcChannelOptions
    {
        Credentials = ChannelCredentials.Create(ChannelCredentials.SecureSsl, callCredentials),
    });

// Every service declared in the .proto files has a generated `<Service>.<Service>Client` type.
var client = new SomeService.SomeServiceClient(channel);
var response = await client.SomeRpcAsync(new SomeRequest());
```

## TLS, mutual TLS and certificates

gRPC encrypts with **TLS**. `OndewoChannelFactory.Create` (namespace `Ondewo.Vtsi.Client.Connection`) builds the
`GrpcChannel` every generated `<Service>.<Service>Client` takes from an `OndewoClientConfig`, with the same rules
as the ONDEWO Python client. It is part of the package's **`net10.0`** build; a `netstandard2.0` consumer (.NET
Framework, older .NET) does not get it, because the APIs it needs (`SocketsHttpHandler`,
`SslClientAuthenticationOptions`, `X509Certificate2.CreateFromPem`) do not exist there.

| Mode                                | `useSecureChannel` | Config fields                                                       |
|-------------------------------------|--------------------|---------------------------------------------------------------------|
| Plaintext (not for production)      | `false`            | none - a client identity is refused                                 |
| TLS, platform trust store           | `true` (default)   | none                                                                |
| TLS, custom CA                      | `true` (default)   | `grpcCert` = PEM of the CA that signed the server certificate       |
| Mutual TLS                          | `true` (default)   | `grpcCert` (or the platform trust store) plus `grpcClientCert` and `grpcClientKey` |

Rules the code enforces:

- The three certificate fields hold **PEM content**, **not file paths**. Read the files yourself
  (`File.ReadAllText`). A `grpcCert` or `grpcClientCert` without any PEM certificate in it - typically a path - is
  refused with an `ArgumentException` that says so.
- `grpcClientCert` and `grpcClientKey` go together: setting only one throws `ArgumentException` when the
  `OndewoClientConfig` is built. Empty strings on both mean no client identity: plain server-authenticated TLS.
- `useSecureChannel: false` with a client identity throws `ArgumentException` instead of silently dropping the
  identity. A plaintext channel is allowed otherwise, and logs a warning naming `host:port` through the
  `ILoggerFactory` you set on `GrpcChannelOptions.LoggerFactory` (nothing is logged without one; the SDK never
  configures logging itself).
- `grpcClientKey` must be an **unencrypted** PEM key (PKCS#8, PKCS#1 or SEC1). An encrypted key, or one that
  belongs to another certificate, is refused with `ArgumentException`. Certificates after the first one in
  `grpcClientCert` are sent along as intermediates.
- With `grpcCert` set, the server certificate must chain to one of the certificates in it - the platform trust
  store is not consulted, and revocation is not checked (as in grpc-core). The host you connect to must match one
  of the certificate's subject alternative names (SAN); there is no name override, so connect by a name in the SAN.
- A bare IPv6 literal host is bracketed (`::1` becomes `https://[::1]:50055`); CRLF line endings in PEMs work.
- No exception message renders a PEM, a key or the config; `OndewoClientConfig.ToString()` prints the key as
  `***REDACTED***` (empty when it is empty) and the certificates only by their length.

```csharp
using System.IO;
using Grpc.Core;
using Grpc.Net.Client;
using Ondewo.Vtsi.Client.Auth;
using Ondewo.Vtsi.Client.Connection;

var config = new OndewoClientConfig(
    host: "ondewo.example.com",
    port: 50055,
    grpcCert: File.ReadAllText("certs/ca.pem"),
    grpcClientCert: File.ReadAllText("certs/client.pem"),  // leave both out for server-authenticated TLS
    grpcClientKey: File.ReadAllText("certs/client.key"));

// One channel for every service client of this server; dispose it when they are all done.
using GrpcChannel channel = OndewoChannelFactory.Create(config, options =>
{
    // Optional: a bearer token on every call, and a logger for gRPC and this SDK.
    options.Credentials = ChannelCredentials.Create(
        ChannelCredentials.SecureSsl, OndewoAuth.CreateBearerCredentials(accessToken));
    options.LoggerFactory = loggerFactory;
});
var client = new SomeService.SomeServiceClient(channel);
```

The `configure` callback runs after the defaults below are applied, so it can change any of them. Replacing
`HttpHandler` or `HttpClient` there discards the TLS setup; `OndewoChannelFactory.CreateHttpHandler(config)` returns
the configured `SocketsHttpHandler` if you build your own `GrpcChannelOptions`.

### Channel defaults

The defaults of the Python client, mapped to what `Grpc.Net.Client` exposes:

| Python channel option                                | .NET                                                         |
|------------------------------------------------------|--------------------------------------------------------------|
| `grpc.keepalive_time_ms=30000`                       | `SocketsHttpHandler.KeepAlivePingDelay` = 30 s               |
| `grpc.http2.ping_timeout_ms=20000`                   | `SocketsHttpHandler.KeepAlivePingTimeout` = 20 s             |
| `grpc.keepalive_permit_without_calls=0`              | `KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests` |
| `grpc.http2.max_pings_without_data=2`                | not exposed by .NET: pings are sent only while calls are active |
| `grpc.keepalive_timeout_ms=20000` (`TCP_USER_TIMEOUT`) | not exposed by .NET; the 20 s ping timeout detects a dead connection |
| `grpc.max_reconnect_backoff_ms=5000`                 | `GrpcChannelOptions.MaxReconnectBackoff` = 5 s               |
| max send / receive message length 2^31-1             | `MaxSendMessageSize` / `MaxReceiveMessageSize` = `int.MaxValue` |
| per-method retry policy (idempotent methods only)    | not configured: `Grpc.Net.Client` retries nothing without a service config |

`EnableMultipleHttp2Connections` is on and pooled connections never idle out, as the `Grpc.Net.Client` docs
recommend for long-lived channels.

### A test PKI with openssl

A CA, a server certificate with SANs, and a client certificate with the `clientAuth` extended key usage. For tests
only: the keys are unencrypted.

```bash
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 365 \
  -subj "/CN=Test CA" -keyout ca.key -out ca.pem

printf 'subjectAltName=DNS:localhost,IP:127.0.0.1\nextendedKeyUsage=serverAuth\n' > server.ext
openssl req -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes \
  -subj "/CN=localhost" -keyout server.key -out server.csr
openssl x509 -req -in server.csr -CA ca.pem -CAkey ca.key -CAcreateserial -days 365 \
  -extfile server.ext -out server.pem

printf 'extendedKeyUsage=clientAuth\n' > client.ext
openssl req -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes \
  -subj "/CN=my-client" -keyout client.key -out client.csr
openssl x509 -req -in client.csr -CA ca.pem -CAkey ca.key -CAcreateserial -days 365 \
  -extfile client.ext -out client.pem

chmod 600 *.key
openssl verify -CAfile ca.pem server.pem client.pem
```

The client then uses `ca.pem` / `client.pem` / `client.key`; a server that requires client certificates uses
`server.pem` / `server.key` and trusts `ca.pem` for its clients. An encrypted key is decrypted with
`openssl pkey -in encrypted.key -out client.key`.

The test suite builds the same kind of PKI in code at test time (`tests/*/TlsTestPki.cs`) and runs real
handshakes against an in-process Kestrel server, so no private key is committed.

### TLS security notes

- `OndewoClientConfig` holds `grpcClientKey` as a plain managed string for the lifetime of the config, and .NET
  cannot wipe it from memory. Load it from a file (mode `0600`, never committed) or a secret store at startup, and
  keep the config's lifetime short if that matters to you.
- `ToString()` redacts the key, but do not log the PEM strings you pass in, or the files they came from.
- This SDK has no serialization of the config, so nothing it does writes the key anywhere.

### TLS troubleshooting

A failed handshake is an `RpcException`; `Grpc.Net.Client` reports a certificate the client refused as
`StatusCode.Internal` (grpc-core and the Python client say `UNAVAILABLE`) and a connection the server dropped as
`StatusCode.Unavailable`. The cause is in `exception.Status.DebugException` and its inner exceptions:

- **`The remote certificate is invalid because of errors in the certificate chain: PartialChain`** (or
  `UntrustedRoot`): `grpcCert` is not the CA that issued the server certificate, or `grpcCert` is empty and the
  server uses a private CA, or the server does not send its intermediate certificates.
- **`The remote certificate is invalid according to the validation procedure: RemoteCertificateNameMismatch`**:
  the host you connect to is not in the server certificate's SAN. Connect by a name in the SAN, or add the SAN.
- **`An HTTP/2 connection could not be established because the server did not complete the HTTP/2 handshake`**
  against a server that requires client certificates: no client certificate was presented, or one the server's CA
  did not issue. The server log names the reason. Set `grpcClientCert` / `grpcClientKey`.
- **`ArgumentException: ... contains no PEM certificate ... not a file path`**: a certificate field holds a path or
  other non-PEM text. Pass `File.ReadAllText(path)`.
- **`ArgumentException: ... could not be loaded as a PEM certificate and its unencrypted private key`**: the key is
  encrypted, malformed, or belongs to another certificate.

## Repository structure

```
.
├── api                                      <----- generated stubs, nested by C# namespace
│   └── Ondewo
│       └── ...
├── auth                                     <----- hand-written: bearer-token helpers
├── connection                               <----- hand-written: TLS / mutual-TLS channel factory (net10.0)
├── tests                                    <----- xunit suite over the committed stubs
├── artifacts                                <----- compiled assembly, symbols, XML docs (not tracked)
├── coverage                                 <----- cobertura/lcov written by `make test` (not tracked)
├── nupkg                                    <----- the packed .nupkg / .snupkg (not tracked)
├── ondewo-vtsi-api                               <----- submodule: the .proto sources
├── ondewo-proto-compiler                    <----- submodule: the code generator
├── Ondewo.VTSI.Client.csproj     <----- generated project file (tracked)
├── Directory.Build.props                    <----- fallback MSBuild pins for a submodule-free build
├── tools                                    <----- dotnet / gh in pinned images, a core-perl zip reader
├── Makefile                                 <----- every documented entry point, see `make help`
├── README.md
└── RELEASE.md
```

## Regenerating the stubs

```shell
make build
```

`make build` is the whole pipeline, and each step is also a documented target of its own:

| Target                                | What it does                                                                     |
| ------------------------------------- | -------------------------------------------------------------------------------- |
| `clean`                               | removes `api/`, `artifacts/`, `nupkg/`, `bin/`, `obj/`                             |
| `update_submodules`                   | `git submodule update --init --recursive`                                          |
| `checkout_defined_submodule_versions`  | checks out the pins at the top of the `Makefile`                                   |
| `build_compiler`                      | builds `ondewo-csharp-proto-compiler:<compiler version>` from the pinned submodule, FROM the digest-pinned .NET SDK image |
| `generate_ondewo_protos`              | runs that image over `ondewo-vtsi-api/ondewo` and writes the library back into the repo  |
| `check_build`                         | fails when a `.proto` produced no `.cs` stub                                        |

Run `make help` for the full list, and `make TEST` to print the resolved versions before you build anything.

Two details are worth knowing:

- The image runs as **root** (it writes into a root-owned directory inside the container), so the files it copies
  out are owned by root. `generate_ondewo_protos` calls `fix_generated_file_ownership` right afterwards, which
  hands exactly the four paths the image owns back to you with a `chown` in a root container of the same image —
  no `sudo` on the host.
- The generated `Ondewo.VTSI.Client.csproj` is **tracked**. On the next run the image finds it in the
  input volume and uses it instead of its own default, which is what lets this repository customise the package —
  so keep any edit you make to it restorable from the compiler image's pre-warmed offline NuGet feed, or the
  restore fails with `NU1101`.

## Building and testing locally

```shell
make build_library     ## dotnet build of the generated project, no docker
make test              ## build_library + the xunit suite, gated on coverage
make pack              ## dotnet pack into nupkg/ (.nupkg + .snupkg)
make publish_dry_run   ## pack + verify the package is publishable, no credential needed
```

None of these needs the submodules or the network beyond NuGet — they work on a plain clone, which is exactly
what CI runs: `.github/workflows/ci.yml` builds and tests the **committed** stubs and never builds the compiler
image. A host without a .NET SDK needs only docker: the `Makefile` then puts `tools/dotnet-in-docker` on `PATH`,
which runs every `dotnet` command in `mcr.microsoft.com/dotnet/sdk:10.0.401-noble`, pinned by digest, as your
own user.

`make test` runs the suite under `tests/` and fails when coverage of the **hand-written** sources drops below
`COVERAGE_THRESHOLD` (100%). Everything under `api/` is excluded from the metric — it is machine output, and a
percentage over it measures the generator rather than this repository — but it is still exercised hard: the suite
round-trips every generated message through its wire format, checks every generated enum starts at its zero
value, and binds every generated service client to a channel, asserting it exposes every RPC its service
descriptor declares. The expected `ondewo.vtsi` services and RPCs, with their streaming shapes, are **derived** from
`ondewo-vtsi-api/ondewo/vtsi/*.proto` when that submodule is checked out (CI checks it out), otherwise from the
committed `api/Ondewo/Vtsi/*Grpc.cs` — never hard-coded counts, so an API release that adds RPCs needs no test
edit; the hand-written RPC lists only pin what must stay. Reports land in `coverage/` as cobertura and lcov.

The generated project file carries no literal versions: it reads `$(OndewoPackageId)`,
`$(OndewoPackageVersion)`, `$(OndewoTargetFramework)` and the three package-version properties as MSBuild
properties. The `Makefile` reads those straight back out of the pinned
`ondewo-proto-compiler/csharp/Dockerfile` and `export`s them, so a host build can never drift from what the image
produces. A clone without that submodule — CI included — has no Dockerfile to read, so `Directory.Build.props`
carries a committed fallback for each pin; it declares them only when they are still empty, so the `Makefile`
always wins, and `make check_dotnet_properties` fails the build if the two ever disagree. Update both together.

### Adding hand-written code

Anything you put in the repository is copied into the image's internal compile directory and picked up by the
SDK's default `Compile` glob, so a hand-written `auth/Something.cs` ships inside the package with no barrel file
to maintain — in C# the assembly *is* the barrel. `auth/OndewoAuth.cs` and `connection/` are the examples in the tree.

The same glob is why `Ondewo.VTSI.Client.csproj` carries

```xml
<Compile Remove="tests/**" />
```

— without it the test sources, and `tests/**/obj/*AssemblyInfo.cs`, are compiled into the library itself.

Hand-written code is what the coverage gate measures, so anything added here needs tests: `make test` fails below
100% line, branch and method coverage of everything outside `api/`.

## Releasing

Bump `ONDEWO_VTSI_VERSION` in the `Makefile`, add a non-empty `RELEASE.md` section under the heading
`## Release ONDEWO VTSI Csharp Client <version>` (matched exactly and anchored, so `9.1.1` never matches a `9.1.10`
heading) ending at the `*****` separator, then:

```shell
make ondewo_release
```

The release host needs only **docker, make, git, perl and curl**. `dotnet` runs in the digest-pinned SDK image when
the host has none (`tools/dotnet-in-docker`), `gh` always runs from a pinned image (`tools/gh-in-docker`: gh
2.100.0 on a digest-pinned alpine, checksum-verified), the packages are read with `tools/zip-entries.pl` instead
of `unzip`, and nothing uses `sudo`.

`make ondewo_release` runs, in this order (pinned by `ReleaseCredentialHygieneTests`):

1. **`spc`** — the release branch and tag do not exist locally.
2. **Credentials** — `run_release_with_devops` clones `ondewo-devops-accounts` into a fresh temporary directory
   *outside* the repository (which is mounted into the compiler and dotnet containers), reads `GITHUB_GH_TOKEN` and
   `NUGET_API_KEY` into the environment and **deletes the clone before the release starts**; a trap deletes it on
   every failure too. When `DEVOPS_ACCOUNT_DIR` names an existing directory (the `ondewo-vtsi-api` driver's one
   clone for all clients), that directory is used and left to its owner.
3. **`release_preflight`** — read-only, runs every check and fails if any did, before anything is built:
   docker/git/perl/curl exist and docker answers; both credentials are set; GitHub accepts the token **and** it may
   push to this repository (`gh api repos/ondewo/ondewo-vtsi-client-csharp --jq .permissions.push`); nuget.org
   accepts the API key — not expired, scoped to push `Ondewo.VTSI.Client` — via
   `POST https://www.nuget.org/api/v2/package/create-verification-key/Ondewo.VTSI.Client/<version>` (uploads
   nothing; the key reaches curl on stdin); the version is neither a tag nor a `release/` branch on origin
   (`git ls-remote --exit-code`) nor on nuget.org (flat container index); and the release notes exist.
4. **The test gate** — `build` (regenerates the stubs), the whole test suite with its coverage gate,
   `publish_dry_run` (pack, nuspec metadata, a consumer restore) and the SDK guard — all before `git add`.
5. **The release commit** — `git diff --cached --quiet || git commit`: nothing to commit is fine, a commit that
   fails stops the release.
6. **Publish and verify** — `push_to_nuget`, then `verify_nuget_published` polls nuget.org (30 x 30 s) until the
   flat container lists the version and its `.nupkg` downloads. Only then does anything reach origin.
7. **`git push`, the release branch, the tag, the GitHub release** (notes from `RELEASE.md` on stdin, refused
   when empty) and finally **`release_verify`**: the tag is on origin, the GitHub release has notes, nuget.org
   serves the version. `make release_verify` can be run on its own at any time; it changes nothing.

`GITHUB_GH_TOKEN` and `NUGET_API_KEY` are read at runtime only and must never be committed. They never reach an
argv or a log, and they are not even in the environment of most steps: the `Makefile` `unexport`s both from its
blanket `export` and hands them back only to the targets that use them (`GITHUB_CREDENTIAL_TARGETS`,
`NUGET_CREDENTIAL_TARGETS`), so `dotnet restore/build/test/pack` — which run third-party package build targets —
never see either, and `tools/dotnet-in-docker` forwards `NUGET_API_KEY` to a `dotnet nuget push` container only.
gh never logs in: every call gets `GH_TOKEN` from `GITHUB_GH_TOKEN` for that one command, so no token is written to
a gh config on the release host and a stale `GH_TOKEN` / `GITHUB_TOKEN` in your shell is never the one used.

### The NuGet half

| Target              | What it does                                                                       |
| ------------------- | ---------------------------------------------------------------------------------- |
| `pack`              | `dotnet pack` into `nupkg/` — the `.nupkg` and the `.snupkg` symbol package          |
| `verify_nupkg_metadata` | reads the nuspec back out of the packed `.nupkg` and fails on missing metadata  |
| `verify_nupkg_installs` | restores the packed `.nupkg` from a local folder feed into a throwaway consumer  |
| `publish_dry_run`   | the three above — **needs no credential**, and is what `ci.yml` runs on every push   |
| `check_nuget_api_key_valid` | read-only: nuget.org accepts `NUGET_API_KEY` for pushing this package        |
| `push_to_nuget`     | `dotnet nuget push` to `NUGET_SOURCE`, **without** `--skip-duplicate`: "already exists" fails |
| `verify_nuget_published` | read-only: polls until nuget.org lists the version and serves its `.nupkg`    |
| `publish`           | `publish_dry_run`, `push_to_nuget`, `verify_nuget_published`                        |

`NUGET_API_KEY` must be an API key scoped to **Push** for the glob pattern `Ondewo.*`, stored in
`ondewo-devops-accounts/account_nuget.env`. nuget.org API keys expire (at most 365 days): when the preflight reports
`nuget.org refused NUGET_API_KEY (HTTP 403)`, renew the key there and in the `NUGET_API_KEY` repository secret.
Pushing the `.nupkg` uploads the `.snupkg` beside it automatically.

Publishing needs **.NET SDK 10.0.400 or newer**: `dotnet nuget push` reads `NUGET_API_KEY` from the environment
only from that SDK on. `check_nuget_push_sdk` checks for it before anything is committed (and `push_to_nuget` again)
instead of pushing without a key. The pinned SDK image and `release.yml`'s `dotnet-version: '10.0.x'` satisfy it.

Pushing a version tag (`8.7.0`, `8.7.0-rc.1` — the shape `make create_release_tag` writes) also triggers
`.github/workflows/release.yml`, which re-runs the test and dry-run gates on the tagged tree. `make release` has
already published by then, so the workflow finds the version on nuget.org, says so and pushes nothing; for a tag
cut any other way it pushes with the `NUGET_API_KEY` **repository secret** (failing loudly without it), and either
way it ends by verifying that nuget.org serves the version.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Commits follow
[Conventional Commits](https://www.conventionalcommits.org/); the `giticket` hook prepends the ticket id from the
branch name, so never write it yourself.

## License

[Apache 2.0](LICENSE)
