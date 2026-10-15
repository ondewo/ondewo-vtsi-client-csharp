export

# =====================================================================================
# ondewo-vtsi-client-csharp - Makefile
#
# Single entry point for regenerating the gRPC client stubs from the ondewo-vtsi-api protos and
# for cutting a release. There is no hand-written application here: `make build` drives the
# ondewo-proto-compiler docker image and everything under api/ is its output.
#
# Quick start:
#   make help                  # list every documented target
#   make makefile_chapters     # list the section headers below
#   make setup_developer_environment_locally
#   make build                 # submodules -> compiler image -> stubs -> NuGet package
#   make test                  # compile the generated library and run the test suite
#
# Versioning: ONDEWO_VTSI_VERSION (below) is the single source of truth. It MUST
# match the ONDEWO VTSI API in major and minor version. It is handed to MSBuild as
# the $(OndewoPackageVersion) property, so no file has to be rewritten when it changes - only
# this Makefile and RELEASE.md.
#
# Overriding variables: pass on the command line, e.g. `make build ONDEWO_VTSI_VERSION=1.2.3`,
# or export in the environment. Credentials (GITHUB_GH_TOKEN, NUGET_API_KEY) are only ever read
# at runtime and must never be committed.
# =====================================================================================

# ---------------- BEFORE RELEASE ----------------
# 1 - Update Version Number (ONDEWO_VTSI_VERSION)
# 2 - Update RELEASE.md (a non-empty "## Release ONDEWO VTSI Csharp Client <version>" section)
# 3 - make build
# -------------- Release Process Steps (make ondewo_release) --------------
# 1 - spc, then read the credentials from ondewo-devops-accounts and DELETE that clone at once
# 2 - release_preflight: every credential is valid, the version is unreleased everywhere (read-only)
# 3 - build, the full test suite, the NuGet dry run and the SDK guard - all before anything is committed
# 4 - local release commit -> push to nuget.org -> wait until nuget.org serves the version
# 5 - git push -> release branch -> tag -> GitHub release -> release_verify

########################################################
# 		Variables
########################################################

# MUST BE THE SAME AS THE API in Major and Minor Version Number
# example: API 1.2.0 --> Client 1.2.X
ONDEWO_VTSI_VERSION=9.1.0

# Submodule pins. Both are checked out by `make checkout_defined_submodule_versions`, so the
# generated code is always reproducible from this file alone.
ONDEWO_VTSI_API_GIT_BRANCH=tags/9.1.0
ONDEWO_PROTO_COMPILER_GIT_BRANCH=tags/5.15.7

# You need to set up an access token at https://github.com/settings/tokens - permissions are important
GITHUB_GH_TOKEN?=ENTER_YOUR_TOKEN_HERE
# You need to set up an API key at https://www.nuget.org/account/apikeys, scoped to
# "Push" for the glob pattern Ondewo.*. Released from ondewo-devops-accounts/account_nuget.env
# (see run_release_with_devops) and from the NUGET_API_KEY GitHub secret in release.yml.
NUGET_API_KEY?=ENTER_HERE_YOUR_NUGET_API_KEY
NUGET_SOURCE?=https://api.nuget.org/v3/index.json

# The `export` at the top hands every variable to every recipe - and so to `dotnet restore/build/test`,
# which runs the build targets of third-party packages. The credentials are therefore taken back out
# of that blanket export (this line must come AFTER the ?= defaults above: an `unexport` of a variable
# that does not exist yet defines it as empty, and the ?= would then never apply) and handed back
# only to the targets below, whose recipes use them. A sub-make re-reads this file and so starts
# unexported again. GH_TOKEN / GITHUB_TOKEN are unexported too: every gh call sets GH_TOKEN itself,
# from GITHUB_GH_TOKEN, so a stale token in the caller's environment can never be the one gh uses.
unexport GITHUB_GH_TOKEN NUGET_API_KEY GH_TOKEN GITHUB_TOKEN
GITHUB_CREDENTIAL_TARGETS=release release_preflight release_verify check_release_credentials \
	check_gh_token_valid build_gh_release push_to_gh verify_gh_release
NUGET_CREDENTIAL_TARGETS=release release_preflight check_release_credentials check_nuget_api_key_valid \
	push_to_nuget
$(GITHUB_CREDENTIAL_TARGETS): export GITHUB_GH_TOKEN:=$(GITHUB_GH_TOKEN)
$(NUGET_CREDENTIAL_TARGETS): export NUGET_API_KEY:=$(NUGET_API_KEY)

# The section of RELEASE.md that becomes the GitHub release body: from the heading of THIS version -
# anchored and with the version quoted (\Q..\E), so 9.1.1 matches neither a 9.1.10 heading nor a
# sentence that mentions the version - down to the ***** separator that ends every entry. NOT down
# to /\*\*/: that matches the first markdown **bold** span inside the entry and silently truncates
# the notes there, with no error from `gh release create`.
RELEASE_NOTES_CMD=perl -ne 'print if /^\#+ Release ONDEWO VTSI Csharp Client \Q${ONDEWO_VTSI_VERSION}\E$$/../^\*{5}/' RELEASE.md
CURRENT_RELEASE_NOTES=`$(RELEASE_NOTES_CMD)`

GH_REPO_PATH=ondewo/ondewo-vtsi-client-csharp
GH_REPO="https://github.com/${GH_REPO_PATH}"
# gh runs from a pinned image (tools/gh-in-docker), so the release needs no gh on the host and every
# release uses the same gh. `make release GH=gh` uses the host's instead.
GH?=$(CURDIR)/tools/gh-in-docker/gh
# The devops-accounts clone. Empty: run_release_with_devops clones into a fresh directory OUTSIDE the
# repository (which is mounted into containers) and deletes it as soon as the credentials are read.
# Set (the ondewo-vtsi-api driver does, to one clone shared by all eleven clients): that directory is
# used as it is and left alone - its owner deletes it.
DEVOPS_ACCOUNT_GIT=ondewo-devops-accounts
DEVOPS_ACCOUNT_DIR?=


# --- Layout
# ONDEWO_API_DIR       submodule holding the .proto sources; it is also the protoc -I root, so it
#                      is what the image gets as its <relative_protos_dir> argument.
# ONDEWO_PROTOS_SUBDIR sub-directory of that root the compilation is scoped to. The sibling
#                      google/ tree stays an import path and is never code-generated: those
#                      descriptors ship in the Google.Api.CommonProtos NuGet package.
ONDEWO_API_DIR=ondewo-vtsi-api
ONDEWO_PROTO_COMPILER_DIR:=ondewo-proto-compiler
ONDEWO_PROTOS_SUBDIR=ondewo
ONDEWO_PROTOS_DIR=${ONDEWO_API_DIR}/${ONDEWO_PROTOS_SUBDIR}

# The compiler image is tagged with the compiler VERSION it was built from (not :latest, which any
# other checkout on the same docker daemon can overwrite with a different compiler); `make
# build_compiler` rebuilds it from the pinned submodule, on the pinned SDK image below.
PROTO_COMPILER_VERSION=$(patsubst tags/%,%,${ONDEWO_PROTO_COMPILER_GIT_BRANCH})
PROTO_COMPILER_IMAGE=ondewo-csharp-proto-compiler:${PROTO_COMPILER_VERSION}
# The ONE .NET SDK image, by exact version AND digest: the compiler image is built FROM it, and
# tools/dotnet-in-docker/dotnet runs it on a host without a .NET SDK (the wrapper carries the same
# default; a test keeps the two equal).
DOTNET_SDK_TAG=10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_TAG}
PROTO_COMPILER_DOCKERFILE:=${ONDEWO_PROTO_COMPILER_DIR}/csharp/Dockerfile

# --- MSBuild properties read by the generated project file
# The generated <PackageId>.csproj deliberately carries no literal version: it reads
# $(OndewoPackageId), $(OndewoPackageVersion), $(OndewoTargetFramework), $(GoogleProtobufVersion),
# $(GrpcDotnetVersion) and $(GoogleApiCommonProtosVersion). Inside the compiler image those come
# from its Dockerfile ARG lines; on the host they are read back OUT of that same pinned Dockerfile
# here, so a host build can never drift from the image. `export` (top of this file) hands every
# one of them to MSBuild, which reads environment variables as properties.
OndewoPackageId=Ondewo.VTSI.Client
OndewoPackageVersion=${ONDEWO_VTSI_VERSION}
# The two artefacts `dotnet pack` produces, named by NuGet's fixed <id>.<version>.<ext> convention.
# The .snupkg is the symbol package: `dotnet nuget push` uploads it automatically when it sits next
# to the .nupkg it belongs to, which is why both live in the same directory.
NUPKG_DIR=nupkg
NUPKG=${NUPKG_DIR}/${OndewoPackageId}.${ONDEWO_VTSI_VERSION}.nupkg
SNUPKG=${NUPKG_DIR}/${OndewoPackageId}.${ONDEWO_VTSI_VERSION}.snupkg
# nuget.org's read-only package content API - what a consumer's restore downloads from - and the
# endpoint that proves an API key may push this package (it mints a short-lived verification key;
# nothing is uploaded). The flat container lower-cases ids.
NUGET_FLATCONTAINER:=https://api.nuget.org/v3-flatcontainer/$(shell echo ${OndewoPackageId} | tr 'A-Z' 'a-z')
NUGET_VERIFICATION_KEY_API=https://www.nuget.org/api/v2/package/create-verification-key/${OndewoPackageId}/${ONDEWO_VTSI_VERSION}
# verify_nuget_published polls this often, this far apart: 30 x 30 s = 15 minutes, which covers
# nuget.org's validation and indexing of a fresh upload.
NUGET_VERIFY_ATTEMPTS?=30
NUGET_VERIFY_INTERVAL_S?=30
# Scratch tree for publish_dry_run: the extracted nuspec and an isolated consumer restore.
DRY_RUN_DIR=.nuget-dry-run
# Keeps the .proto submodule out of the SDK's default Compile glob on a host build.
OndewoProtosDir=${ONDEWO_API_DIR}
# In a plain clone - CI included - the submodule is not checked out and every $(shell sed ...)
# below yields the empty string. Directory.Build.props then supplies a committed fallback for each
# of them (it declares them only when they are still empty, so an exported value here always
# wins), and `make check_dotnet_properties` fails the build when the two ever disagree.
DOTNET_PROPS_FILE=Directory.Build.props
OndewoTargetFramework:=$(shell sed -n 's|^ARG DOTNET_TARGET_FRAMEWORK=||p' ${PROTO_COMPILER_DOCKERFILE} 2>/dev/null)
GoogleProtobufVersion:=$(shell sed -n 's|^ARG GOOGLE_PROTOBUF_VERSION=||p' ${PROTO_COMPILER_DOCKERFILE} 2>/dev/null)
GrpcDotnetVersion:=$(shell sed -n 's|^ARG GRPC_DOTNET_VERSION=||p' ${PROTO_COMPILER_DOCKERFILE} 2>/dev/null)
GoogleApiCommonProtosVersion:=$(shell sed -n 's|^ARG GOOGLE_API_COMMONPROTOS_VERSION=||p' ${PROTO_COMPILER_DOCKERFILE} 2>/dev/null)

# The .NET SDK: the host's own when it has one (CI installs it with setup-dotnet), otherwise the pinned
# SDK image behind tools/dotnet-in-docker/dotnet, which every `dotnet` recipe line then resolves to.
# A release machine without an SDK used to get as far as pushing the release tag and then die in
# `make publish` with "dotnet: No such file or directory".
ifeq ($(shell command -v dotnet 2>/dev/null),)
export PATH := $(CURDIR)/tools/dotnet-in-docker:$(PATH)
endif

# `make` with no target prints the help listing.
.DEFAULT_GOAL := help

# Define colors globally (reused for [INFO]/[SUCCESS]/[WARN]/[ERROR] log lines in recipes)
BLUE   := \033[1;34m
GREEN  := \033[0;32m
YELLOW := \033[1;33m
RED    := \033[0;31m
NC     := \033[0m

########################################################
#       ONDEWO Standard Make Targets
########################################################

setup_developer_environment_locally: update_submodules install_precommit_hooks ## Ready a fresh laptop: check out the submodules and install the pre-commit hooks

install_precommit_hooks: ## Installs pre-commit hooks and sets them up for the ondewo-vtsi-client-csharp repo
	pip install pre-commit
	pre-commit install
	pre-commit install --hook-type commit-msg

precommit_hooks_run_all_files: ## Runs all pre-commit hooks on all files and not just the changed ones
	pre-commit run --all-files

help: ## Print usage info about help targets
	# (first comment after target starting with double hashes ##)
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' Makefile | sort | awk 'BEGIN {FS = ":.*?## "}; {printf "\033[36m%-40s\033[0m %s\n", $$1, $$2}'

makefile_chapters: ## Shows all sections of Makefile
	@echo `cat Makefile| grep "########################################################" -A 1 | grep -v "########################################################"`

TEST: ## Diagnostics - print the resolved build configuration and the current release notes
	@echo "Client version:       ${ONDEWO_VTSI_VERSION}"
	@echo "API submodule pin:    ${ONDEWO_VTSI_API_GIT_BRANCH}"
	@echo "Compiler pin:         ${ONDEWO_PROTO_COMPILER_GIT_BRANCH}"
	@echo "Compiler image:       ${PROTO_COMPILER_IMAGE}"
	@echo "NuGet package id:     ${OndewoPackageId}"
	@echo "Target framework:     ${OndewoTargetFramework}"
	@echo "Google.Protobuf:      ${GoogleProtobufVersion}"
	@echo "Grpc.Net.Client:      ${GrpcDotnetVersion}"
	@echo "Google.Api.CommonProtos: ${GoogleApiCommonProtosVersion}"
	@echo "NuGet source:         ${NUGET_SOURCE}"
	@if [ -n "$${GITHUB_GH_TOKEN}" ] && [ "$${GITHUB_GH_TOKEN}" != "ENTER_YOUR_TOKEN_HERE" ]; then \
		echo "GITHUB_GH_TOKEN set:  yes"; else echo "GITHUB_GH_TOKEN set:  no"; fi
	@if [ -n "$${NUGET_API_KEY}" ] && [ "$${NUGET_API_KEY}" != "ENTER_HERE_YOUR_NUGET_API_KEY" ]; then \
		echo "NUGET_API_KEY set:    yes"; else echo "NUGET_API_KEY set:    no"; fi
	@printf '\n%s\n' "${CURRENT_RELEASE_NOTES}"

########################################################
#       Repo Specific Make Targets
########################################################
#		Build

build: clean update_submodules checkout_defined_submodule_versions build_compiler generate_ondewo_protos check_build ## Build the client library: submodules -> compiler image -> stubs -> NuGet package
	@echo "$(GREEN)[SUCCESS]$(NC) ${OndewoPackageId} ${ONDEWO_VTSI_VERSION} built"

build_compiler: ## Build the proto compiler docker image from the pinned submodule
	@echo "$(BLUE)[INFO]$(NC) Building ${PROTO_COMPILER_IMAGE} from ${ONDEWO_PROTO_COMPILER_DIR}/csharp ..."
	@test -f ${PROTO_COMPILER_DOCKERFILE} || { \
		echo "$(RED)[ERROR]$(NC) ${PROTO_COMPILER_DOCKERFILE} is missing - run 'make update_submodules' first"; \
		exit 1; \
	}
# Built directly rather than through the submodule's build.sh, which tags :latest, passes --no-cache
# and builds FROM the floating sdk:10.0 tag.
	docker build -t ${PROTO_COMPILER_IMAGE} \
		--build-arg DOTNET_SDK_VERSION=${DOTNET_SDK_TAG} \
		${ONDEWO_PROTO_COMPILER_DIR}/csharp

# Derived from ondewo-proto-compiler/csharp/example/run-compile.sh - same image tag and the same
# three positional arguments <relative_protos_dir> <target_subdir> <package_id>.
#   * NO -it: it breaks every non-interactive caller ("cannot attach stdin to a TTY-enabled
#     container because stdin is not a terminal"). Keep -it only on an interactive
#     --entrypoint /bin/bash debug run.
#   * The repository IS the input volume: the image copies it into an internal compile directory
#     and compiles there, so nothing mounted is ever mutated, and any hand-written .cs shipped
#     beside the stubs is picked up by the SDK's default Compile glob.
#   * The repository is also the output volume: the image writes api/, artifacts/ and nupkg/ and
#     wipes exactly those three first, so a renamed or deleted proto leaves no orphan behind.
#   * -e OndewoPackageVersion overrides the image default (which is the COMPILER version) with
#     this client's version, so the packed .nupkg carries the right number.
generate_ondewo_protos: ## Generate the csharp gRPC client stubs and the NuGet package from the API protos
	@test -d ${ONDEWO_PROTOS_DIR} || { \
		echo "$(RED)[ERROR]$(NC) ${ONDEWO_PROTOS_DIR} is missing - run 'make update_submodules' first"; \
		exit 1; \
	}
# The whole repository is mounted into a root container below, so it must never hold the
# credentials clone (run_release_with_devops keeps it outside; this catches a leftover old-style one).
	@test ! -e ${DEVOPS_ACCOUNT_GIT} || { \
		echo "$(RED)[ERROR]$(NC) ./${DEVOPS_ACCOUNT_GIT} exists - delete it: it would be mounted into the compiler container"; \
		exit 1; \
	}
	@echo "$(BLUE)[INFO]$(NC) Generating csharp stubs from ${ONDEWO_PROTOS_DIR} into api/ ..."
	docker run --rm \
		-e OndewoPackageVersion=${ONDEWO_VTSI_VERSION} \
		-v ${shell pwd}:/input-volume \
		-v ${shell pwd}:/output-volume \
		${PROTO_COMPILER_IMAGE} "${ONDEWO_API_DIR}" "${ONDEWO_PROTOS_SUBDIR}" "${OndewoPackageId}"
	@$(MAKE) fix_generated_file_ownership
	@echo "$(GREEN)[SUCCESS]$(NC) Generated api/, ${OndewoPackageId}.csproj, artifacts/ and nupkg/"

# The csharp image writes under root-owned /image-data, so - unlike the python target - its
# container must NOT run with --user, and everything it copies to the output volume lands owned
# by root. Ownership is handed back by a root container too (the compiler image itself, entrypoint
# chown), so no sudo is needed on the host. Only the paths the image owns are touched, never the
# whole working tree.
fix_generated_file_ownership: ## Take back ownership of the files the compiler container wrote as root
	@for path in api artifacts nupkg ${OndewoPackageId}.csproj ; do \
		if [ -e "$$path" ] && [ "$$(stat -c %u:%g "$$path")" != "$$(id -u):$$(id -g)" ]; then \
			echo "$(BLUE)[INFO]$(NC) Taking ownership of $$path ..." ; \
			docker run --rm --entrypoint chown -v "$(CURDIR)/$$path:/owned/$$path" \
				${PROTO_COMPILER_IMAGE} -R "$$(id -u):$$(id -g)" "/owned/$$path" || exit 1 ; \
		fi ; \
	done

# protoc emits one <PascalCase(basename)>.cs per proto plus one <PascalCase(basename)>Grpc.cs per
# proto that declares a service, so the file count is NOT a fixed multiple of the proto count.
# Both sides are therefore normalised (lower-cased, separators removed) before they are compared.
check_build: ## Checks that a csharp stub was generated for every compiled proto
	@test -d api || { \
		echo "$(RED)[ERROR]$(NC) no api/ directory - run 'make generate_ondewo_protos' first"; \
		exit 1; \
	}
	@find api -type f -name '*.cs' -exec basename {} .cs ';' \
		| sed -e 's/Grpc$$//' | tr -d '_-' | tr 'A-Z' 'a-z' | sort -u > build_check.txt
	@rc=0 ; \
	for proto in $$(find ${ONDEWO_PROTOS_DIR} -type f -name '*.proto') ; do \
		want=$$(basename "$$proto" .proto | tr -d '_-' | tr 'A-Z' 'a-z') ; \
		grep -qx "$$want" build_check.txt || { echo "$(RED)[ERROR]$(NC) No csharp stub for $$proto" ; rc=1 ; } ; \
	done ; \
	rm -f build_check.txt ; \
	if [ $$rc -ne 0 ]; then exit 1 ; fi ; \
	echo "$(GREEN)[SUCCESS]$(NC) every proto under ${ONDEWO_PROTOS_DIR} has a generated stub"

# The generated project file has no literal versions - it reads them as MSBuild properties. They
# are pinned in ONE place, the compiler's Dockerfile ARG lines, and mirrored into
# ${DOTNET_PROPS_FILE} so that a checkout without submodules still builds. Drift between the two
# would silently compile the committed stubs against a different package graph than the image that
# generated them, so it is an error here rather than a surprise at run time.
check_dotnet_properties: ## Verify the committed MSBuild pins match the pinned compiler Dockerfile
	@if [ ! -f ${PROTO_COMPILER_DOCKERFILE} ]; then \
		echo "$(RED)[ERROR]$(NC) ${PROTO_COMPILER_DOCKERFILE} is not checked out, so the pins committed in" ; \
		echo "        ${DOTNET_PROPS_FILE} cannot be compared against anything and this target" ; \
		echo "        cannot do its job. It used to warn and exit 0 here, which made it a no-op in" ; \
		echo "        every submodule-free checkout - CI included - so it never once verified a pin." ; \
		echo "        Check the one submodule out and re-run:" ; \
		echo "            git submodule update --init ${ONDEWO_PROTO_COMPILER_DIR}" ; \
		exit 1 ; \
	fi ; \
	rc=0 ; \
	for pair in "OndewoTargetFramework=DOTNET_TARGET_FRAMEWORK" \
	            "GoogleProtobufVersion=GOOGLE_PROTOBUF_VERSION" \
	            "GrpcDotnetVersion=GRPC_DOTNET_VERSION" \
	            "GoogleApiCommonProtosVersion=GOOGLE_API_COMMONPROTOS_VERSION" ; do \
		property=$${pair%%=*} ; \
		argument=$${pair##*=} ; \
		pinned=$$(sed -n "s|^ARG $$argument=||p" ${PROTO_COMPILER_DOCKERFILE}) ; \
		committed=$$(sed -n "s|.*<$$property[^>]*>\(.*\)</$$property>.*|\1|p" ${DOTNET_PROPS_FILE}) ; \
		if [ -z "$$pinned" ] || [ "$$pinned" != "$$committed" ]; then \
			echo "$(RED)[ERROR]$(NC) $$property is '$$committed' in ${DOTNET_PROPS_FILE} but" ; \
			echo "        ARG $$argument='$$pinned' in ${PROTO_COMPILER_DOCKERFILE} - update the former" ; \
			rc=1 ; \
		fi ; \
	done ; \
	if [ $$rc -ne 0 ]; then exit 1 ; fi ; \
	echo "$(GREEN)[SUCCESS]$(NC) the committed MSBuild pins match ${PROTO_COMPILER_DOCKERFILE}"

build_library: check_dotnet_properties ## Compile the generated library on the host (no docker)
	@test -f ${OndewoPackageId}.csproj || { \
		echo "$(RED)[ERROR]$(NC) ${OndewoPackageId}.csproj is missing - run 'make build' first"; \
		exit 1; \
	}
	dotnet build ${OndewoPackageId}.csproj -c Release

# Coverage is measured over the HAND-WRITTEN sources only: everything under api/ is machine
# output, and a coverage number over generated code measures the generator, not this repository.
# The stubs are still EXERCISED by the suite - it round-trips every generated message through the
# wire format and binds every generated service client to a channel - they are just not counted.
# `Include` narrows instrumentation to the client assembly (the test assembly itself and the
# Google.Protobuf / Grpc.* dependencies are none of our business) and `ExcludeByFile` then drops
# the generated half of it, which leaves exactly auth/.
# The commas in the coverlet switches are written `%2c`: MSBuild splits a /p: value on a literal
# comma and aborts with "MSB1006: Property is not valid. Switch: lcov".
COVERAGE_THRESHOLD?=100
TEST_PROJECT=tests/Ondewo.VTSI.Client.Tests/Ondewo.VTSI.Client.Tests.csproj

test: build_library ## Run the csharp test suite over the committed stubs, gated on hand-written coverage
	@test -f ${TEST_PROJECT} || { \
		echo "$(RED)[ERROR]$(NC) ${TEST_PROJECT} is missing - this repository must ship a test project"; \
		exit 1; \
	}
	@echo "$(BLUE)[INFO]$(NC) dotnet test ${TEST_PROJECT} (coverage gate: ${COVERAGE_THRESHOLD}% of the hand-written sources)"
	dotnet test ${TEST_PROJECT} -c Release \
		/p:CollectCoverage=true \
		/p:Include="[${OndewoPackageId}]*" \
		/p:ExcludeByFile="**/api/**/*.cs" \
		/p:CoverletOutput=${CURDIR}/coverage/ \
		/p:CoverletOutputFormat=cobertura%2clcov \
		/p:Threshold=${COVERAGE_THRESHOLD} \
		/p:ThresholdType=line%2cbranch%2cmethod \
		/p:ThresholdStat=total
	@echo "$(GREEN)[SUCCESS]$(NC) test suite passed with >= ${COVERAGE_THRESHOLD}% coverage of the hand-written sources"

# -warnaserror promotes the NU5xxx packaging warnings to errors, which is what turns "nuget.org
# warns about a missing licence/readme" into a failed build here instead of a published package
# nobody wants. It is safe to be this strict only because --no-build implies --no-restore: the
# restore-time NuGet audit advisories (NU19xx), which appear when a CVE is published for a
# dependency and have nothing to do with packaging, cannot reach this step.
pack: build_library ## Pack the NuGet package (.nupkg + .snupkg) on the host into nupkg/
	dotnet pack ${OndewoPackageId}.csproj -c Release --no-build -o ${NUPKG_DIR} -warnaserror

clean: ## Remove the generated stubs, the build output and the packed packages
	rm -rf api artifacts ${NUPKG_DIR} bin obj coverage build_check.txt ${DRY_RUN_DIR}

########################################################
#		Submodules

update_submodules: ## Initialize and update all submodules
	@echo "$(BLUE)[INFO]$(NC) START initializing submodules ..."
	git submodule update --init --recursive
	@echo "$(GREEN)[SUCCESS]$(NC) DONE initializing submodules"

checkout_defined_submodule_versions: ## Check out the submodule versions pinned at the top of this Makefile
	@echo "$(BLUE)[INFO]$(NC) START checking out submodules ..."
	git -C ${ONDEWO_API_DIR} fetch --all
	git -C ${ONDEWO_API_DIR} checkout ${ONDEWO_VTSI_API_GIT_BRANCH}
	git -C ${ONDEWO_PROTO_COMPILER_DIR} fetch --all
	git -C ${ONDEWO_PROTO_COMPILER_DIR} checkout ${ONDEWO_PROTO_COMPILER_GIT_BRANCH}
	@echo "$(GREEN)[SUCCESS]$(NC) DONE checking out submodules"

########################################################
#		Release

# The whole release, in the order a repository test (ReleaseOrderTests) pins:
#   release_preflight          read-only: credentials valid, version unreleased, notes present, tools
#   build -> test -> publish_dry_run -> check_nuget_push_sdk
#                              everything that can fail without side effects fails HERE, while the
#                              release is still a no-op (the 9.1.0 release only packed and checked the
#                              package after the tag was on origin)
#   local commit               `git diff --cached --quiet ||`: nothing to commit is fine, a commit that
#                              FAILS (no identity, say) stops the release instead of tagging the
#                              previous commit, which `-git commit` used to do
#   push_to_nuget -> verify_nuget_published
#                              nuget.org is driven from this host, so it goes BEFORE anything reaches
#                              origin: a version nuget.org rejects leaves no branch or tag behind, and
#                              the tag - which triggers release.yml - only appears once nuget.org
#                              serves the package (release.yml then finds it there and pushes nothing)
#   git push -> release branch -> tag -> GitHub release -> release_verify
release: ## Automate the entire release process
	@echo "$(BLUE)[INFO]$(NC) Start release ${ONDEWO_VTSI_VERSION}"
	$(MAKE) release_preflight
	$(MAKE) build
	-$(MAKE) precommit_hooks_run_all_files
	git status
	$(MAKE) check_build
	$(MAKE) test
	$(MAKE) publish_dry_run
	$(MAKE) check_nuget_push_sdk
	git add api
	git add ${OndewoPackageId}.csproj
	git add Makefile
	git add README.md
	git add RELEASE.md
# tests/ is NOT packaged, but leaving it out of the release commit means a regression test written
# alongside a fix never reaches the repository and CI never runs it.
	git add tests
	git add ${ONDEWO_PROTO_COMPILER_DIR}
	git add ${ONDEWO_API_DIR}
	git status
	git diff --cached --quiet || git commit --no-verify -m "chore(release): prepare release ${ONDEWO_VTSI_VERSION}"
	$(MAKE) push_to_nuget
	$(MAKE) verify_nuget_published
	git push
	$(MAKE) create_release_branch
	$(MAKE) create_release_tag
	$(MAKE) push_to_gh
	$(MAKE) release_verify
	@echo "$(GREEN)[SUCCESS]$(NC) Release ${ONDEWO_VTSI_VERSION} finished"

# Read-only. Every check runs - a failure does not hide the next one - and the release stops if any
# failed, before anything is built, committed, pushed or published.
release_preflight: ## Read-only release gate: credentials valid, version unreleased, notes present, host tools
	@rc=0 ; \
	for check in check_release_tools check_release_credentials check_gh_token_valid check_nuget_api_key_valid \
	             check_version_unreleased check_release_notes ; do \
		$(MAKE) --no-print-directory $$check || rc=1 ; \
	done ; \
	if [ $$rc -ne 0 ]; then \
		echo "$(RED)[ERROR]$(NC) release preflight FAILED - nothing was built, committed, pushed or published" ; \
		exit 1 ; \
	fi ; \
	echo "$(GREEN)[SUCCESS]$(NC) release preflight passed for ${ONDEWO_VTSI_VERSION}"

# Read-only. What a finished release must look like from the outside.
release_verify: verify_tag_on_origin verify_gh_release verify_nuget_published ## Read-only: the tag is on origin, the GitHub release has notes, nuget.org serves the version
	@echo "$(GREEN)[SUCCESS]$(NC) ${ONDEWO_VTSI_VERSION} is tagged, released on GitHub and served by nuget.org"

# docker, make, git, perl and curl are all the release needs. dotnet runs in the pinned SDK image when
# the host has none (tools/dotnet-in-docker), gh always in its pinned image (tools/gh-in-docker),
# unzip is replaced by tools/zip-entries.pl, and no step uses sudo.
check_release_tools: ## Assert the host tools the release needs exist (docker, git, perl, curl) and docker answers
	@rc=0 ; \
	for tool in docker git perl curl ; do \
		command -v $$tool >/dev/null 2>&1 || { echo "$(RED)[ERROR]$(NC) '$$tool' is not on PATH - the release needs it" ; rc=1 ; } ; \
	done ; \
	if [ $$rc -eq 0 ] && ! docker info >/dev/null 2>&1; then \
		echo "$(RED)[ERROR]$(NC) docker is installed but the daemon does not answer (docker info failed)" ; rc=1 ; \
	fi ; \
	if [ $$rc -ne 0 ]; then exit 1 ; fi ; \
	echo "$(GREEN)[SUCCESS]$(NC) docker, git, perl and curl are available (dotnet: $$(command -v dotnet), gh: ${GH})"

check_release_credentials: ## Assert both release credentials are set (not empty, not the placeholder)
	@rc=0 ; \
	if [ -z "$${GITHUB_GH_TOKEN}" ] || [ "$${GITHUB_GH_TOKEN}" = "ENTER_YOUR_TOKEN_HERE" ]; then \
		echo "$(RED)[ERROR]$(NC) GITHUB_GH_TOKEN is not set - create one at https://github.com/settings/tokens" ; \
		rc=1 ; \
	fi ; \
	if [ -z "$${NUGET_API_KEY}" ] || [ "$${NUGET_API_KEY}" = "ENTER_HERE_YOUR_NUGET_API_KEY" ]; then \
		echo "$(RED)[ERROR]$(NC) NUGET_API_KEY is not set - create one at https://www.nuget.org/account/apikeys" ; \
		echo "        scoped to Push for the glob pattern 'Ondewo.*', and add it to" ; \
		echo "        ondewo-devops-accounts/account_nuget.env (make ondewo_release reads it from there)" ; \
		rc=1 ; \
	fi ; \
	if [ $$rc -ne 0 ]; then \
		echo "$(RED)[ERROR]$(NC) refusing to start a release that cannot finish it" ; \
		exit 1 ; \
	fi ; \
	echo "$(GREEN)[SUCCESS]$(NC) both release credentials are set"

# GET /repos/<owner>/<repo> changes nothing. `.permissions` is only present for an authenticated
# caller, and `push` is exactly what the release branch, the tag and the GitHub release need.
check_gh_token_valid: ## Assert GitHub accepts GITHUB_GH_TOKEN and lets it push to this repository (read-only)
	@push=$$(GH_TOKEN="$$GITHUB_GH_TOKEN" $(GH) api "repos/${GH_REPO_PATH}" --jq .permissions.push) \
		|| { echo "$(RED)[ERROR]$(NC) GitHub refused GITHUB_GH_TOKEN (see the gh error above)"; exit 1; }; \
	if [ "$$push" != "true" ]; then \
		echo "$(RED)[ERROR]$(NC) GITHUB_GH_TOKEN is valid but may not push to ${GH_REPO_PATH} (permissions.push=$$push)"; \
		exit 1; \
	fi; \
	echo "$(GREEN)[SUCCESS]$(NC) GitHub accepts GITHUB_GH_TOKEN and it may push to ${GH_REPO_PATH}"

# POST .../create-verification-key/<id>/<version> answers 200 only for a key that exists, has not
# expired and is scoped to push this package id; it mints a one-hour verification key and uploads
# nothing. The key reaches curl on stdin (`-K -`, printf is a shell builtin), never on an argv, and
# the response - which carries the minted key - is discarded unread.
check_nuget_api_key_valid: ## Assert nuget.org accepts NUGET_API_KEY for pushing this package (uploads nothing)
	@if [ -z "$${NUGET_API_KEY}" ] || [ "$${NUGET_API_KEY}" = "ENTER_HERE_YOUR_NUGET_API_KEY" ]; then \
		echo "$(RED)[ERROR]$(NC) NUGET_API_KEY is not set"; exit 1; \
	fi; \
	code=$$(printf 'header = "X-NuGet-ApiKey: %s"\n' "$$NUGET_API_KEY" \
		| curl -sS --connect-timeout 20 --max-time 60 -K - -X POST -d '' -o /dev/null -w '%{http_code}' \
			"${NUGET_VERIFICATION_KEY_API}") || code="no answer"; \
	case "$$code" in \
		200) echo "$(GREEN)[SUCCESS]$(NC) nuget.org accepts NUGET_API_KEY for pushing ${OndewoPackageId}" ;; \
		401|403) echo "$(RED)[ERROR]$(NC) nuget.org refused NUGET_API_KEY (HTTP $$code): it is wrong, EXPIRED, or not scoped to push ${OndewoPackageId}"; \
			echo "        renew it at https://www.nuget.org/account/apikeys (Push, glob Ondewo.*) and update ondewo-devops-accounts/account_nuget.env"; \
			exit 1 ;; \
		*) echo "$(RED)[ERROR]$(NC) unexpected answer from nuget.org (HTTP $$code) - cannot tell whether NUGET_API_KEY is valid"; exit 1 ;; \
	esac

# Anchored: `git ls-remote --exit-code` exits 2 when the exact ref does not exist, 0 when it does,
# and anything else when origin could not be asked - which is a failure too, never a pass.
check_version_unreleased: ## Assert ONDEWO_VTSI_VERSION is no tag or release branch on origin and not on nuget.org (read-only)
	@rc=0 ; \
	for ref in "refs/tags/${ONDEWO_VTSI_VERSION}" "refs/heads/release/${ONDEWO_VTSI_VERSION}" ; do \
		git ls-remote --exit-code origin "$$ref" >/dev/null 2>&1 ; status=$$? ; \
		case $$status in \
			0) echo "$(RED)[ERROR]$(NC) $$ref already exists on origin" ; rc=1 ;; \
			2) ;; \
			*) echo "$(RED)[ERROR]$(NC) cannot ask origin for $$ref (git ls-remote exit $$status)" ; rc=1 ;; \
		esac ; \
	done ; \
	index=$$(mktemp) ; trap 'rm -f "$$index"' EXIT ; \
	code=$$(curl -sS --connect-timeout 20 --max-time 60 -o "$$index" -w '%{http_code}' "${NUGET_FLATCONTAINER}/index.json") || code="no answer" ; \
	case "$$code" in \
		200) if grep -Fq '"${ONDEWO_VTSI_VERSION}"' "$$index" ; then \
				echo "$(RED)[ERROR]$(NC) ${OndewoPackageId} ${ONDEWO_VTSI_VERSION} is ALREADY on nuget.org - a version can never be pushed twice" ; rc=1 ; \
			fi ;; \
		404) ;; \
		*) echo "$(RED)[ERROR]$(NC) cannot read ${NUGET_FLATCONTAINER}/index.json (HTTP $$code)" ; rc=1 ;; \
	esac ; \
	if [ $$rc -ne 0 ]; then exit 1 ; fi ; \
	echo "$(GREEN)[SUCCESS]$(NC) ${ONDEWO_VTSI_VERSION} is neither on origin nor on nuget.org"

create_release_branch: ## Create Release Branch and push it to origin
	git checkout -b "release/${ONDEWO_VTSI_VERSION}"
	git push -u origin "release/${ONDEWO_VTSI_VERSION}"

create_release_tag: ## Create Release Tag and push it to origin
	git tag -a ${ONDEWO_VTSI_VERSION} -m "release/${ONDEWO_VTSI_VERSION}"
	git push origin ${ONDEWO_VTSI_VERSION}

# `gh release create` with empty notes succeeds and publishes an EMPTY release, so an entry that was
# forgotten - or a heading whose wording drifted away from what RELEASE_NOTES_CMD matches - is
# otherwise only noticed by whoever reads the release page afterwards.
check_release_notes: ## Assert RELEASE.md has a non-empty section for ONDEWO_VTSI_VERSION
	@notes="$(CURRENT_RELEASE_NOTES)"; \
	body=$$(printf '%s\n' "$$notes" | sed -e '1d' -e '/^\*\{5\}/d' | tr -d '[:space:]'); \
	if [ -z "$$notes" ] || [ -z "$$body" ]; then \
		echo "$(RED)[ERROR]$(NC) RELEASE.md has no non-empty '## Release ONDEWO VTSI Csharp Client ${ONDEWO_VTSI_VERSION}' section"; \
		echo "        The GitHub release would be created with empty notes - add the entry, under that"; \
		echo "        exact heading and terminated by the ***** separator, before releasing."; \
		exit 1; \
	fi; \
	echo "$(GREEN)[SUCCESS]$(NC) RELEASE.md has release notes for ${ONDEWO_VTSI_VERSION}"

# The notes reach gh on stdin (--notes-file -), so no quote, backtick or $ in RELEASE.md can break
# or change the command line. GH_TOKEN is set for this one command, from GITHUB_GH_TOKEN; there is
# no `gh auth login`, so no token is ever written to ~/.config/gh on the release host.
build_gh_release: check_release_notes ## Create the GitHub release with the RELEASE.md section as its notes
	$(RELEASE_NOTES_CMD) | GH_TOKEN="$$GITHUB_GH_TOKEN" $(GH) release create "${ONDEWO_VTSI_VERSION}" \
		--repo ${GH_REPO_PATH} --verify-tag --title "Release ${ONDEWO_VTSI_VERSION}" --notes-file -

push_to_gh: build_gh_release ## Create the GitHub release
	@echo 'Released to Github'

verify_tag_on_origin: ## Assert the release tag exists on origin (read-only)
	@git ls-remote --exit-code origin "refs/tags/${ONDEWO_VTSI_VERSION}" >/dev/null || { \
		echo "$(RED)[ERROR]$(NC) the tag ${ONDEWO_VTSI_VERSION} is not on origin"; exit 1; }
	@echo "$(GREEN)[SUCCESS]$(NC) the tag ${ONDEWO_VTSI_VERSION} is on origin"

verify_gh_release: ## Assert the GitHub release exists and its notes are not empty (read-only)
	@body=$$(GH_TOKEN="$$GITHUB_GH_TOKEN" $(GH) release view "${ONDEWO_VTSI_VERSION}" --repo ${GH_REPO_PATH} --json body --jq .body) \
		|| { echo "$(RED)[ERROR]$(NC) there is no GitHub release ${ONDEWO_VTSI_VERSION} (see the gh error above)"; exit 1; }; \
	if [ -z "$$(printf '%s' "$$body" | tr -d '[:space:]')" ]; then \
		echo "$(RED)[ERROR]$(NC) the GitHub release ${ONDEWO_VTSI_VERSION} has EMPTY notes"; exit 1; \
	fi; \
	echo "$(GREEN)[SUCCESS]$(NC) the GitHub release ${ONDEWO_VTSI_VERSION} exists and has notes"

########################################################
#		NUGET

# `release` runs the same steps one by one, publish_dry_run long before anything is pushed.
publish: publish_dry_run push_to_nuget verify_nuget_published ## Verify the packed package, publish it to nuget.org and wait until nuget.org serves it
	@echo "$(GREEN)[SUCCESS]$(NC) ${OndewoPackageId} ${ONDEWO_VTSI_VERSION} published to ${NUGET_SOURCE}"

# The credential-free half of `publish`, and the only half CI ever runs. It exercises the whole
# packaging path - pack, metadata, installability - so a package that nuget.org would reject, or
# that nobody could consume, fails on an ordinary push instead of in the middle of a release.
publish_dry_run: pack verify_nupkg_metadata verify_nupkg_installs ## Exercise the full packaging path without any credential
	@echo "$(GREEN)[SUCCESS]$(NC) ${NUPKG} is ready to publish - run 'make publish' with NUGET_API_KEY set"

# Asserts on the PACKED artefact rather than on the .csproj: a property can be silently dropped on
# the way into the nuspec (PackageReadmeFile behind a false Exists() condition is exactly that), and
# the nuspec is what nuget.org reads. Every element below is one nuget.org warns about - or, for
# licence and description, rejects the upload over - when it is missing.
verify_nupkg_metadata: ## Assert the packed .nupkg/.snupkg carry the metadata nuget.org expects
	@test -f "${NUPKG}" || { \
		echo "$(RED)[ERROR]$(NC) ${NUPKG} is missing - run 'make pack' first"; \
		exit 1; \
	}
	@test -f "${SNUPKG}" || { \
		echo "$(RED)[ERROR]$(NC) ${SNUPKG} is missing - IncludeSymbols and SymbolPackageFormat=snupkg"; \
		echo "        must stay set in ${OndewoPackageId}.csproj"; \
		exit 1; \
	}
# The packages are read with tools/zip-entries.pl (core perl), so no unzip is needed on the host.
	@mkdir -p ${DRY_RUN_DIR}
	@perl tools/zip-entries.pl cat "${NUPKG}" "${OndewoPackageId}.nuspec" > ${DRY_RUN_DIR}/packed.nuspec
	@perl tools/zip-entries.pl list "${NUPKG}"  > ${DRY_RUN_DIR}/nupkg.list
	@perl tools/zip-entries.pl list "${SNUPKG}" > ${DRY_RUN_DIR}/snupkg.list
	@echo "$(BLUE)[INFO]$(NC) Checking the nuspec of ${NUPKG} ..."
	@rc=0 ; \
	require() { \
		if grep -Eq "$$2" "$$3" ; then \
			echo "  $(GREEN)ok$(NC)      $$1" ; \
		else \
			echo "  $(RED)MISSING$(NC) $$1 - no match for /$$2/ in $$3" ; \
			rc=1 ; \
		fi ; \
	} ; \
	nuspec=${DRY_RUN_DIR}/packed.nuspec ; \
	require "PackageId"              "<id>${OndewoPackageId}</id>"                    "$$nuspec" ; \
	require "Version"                "<version>${ONDEWO_VTSI_VERSION}</version>"       "$$nuspec" ; \
	require "Authors"                "<authors>.+</authors>"                          "$$nuspec" ; \
	require "Description"            "<description>.+</description>"                  "$$nuspec" ; \
	require "PackageLicenseExpression" "<license type=\"expression\">.+</license>"     "$$nuspec" ; \
	require "PackageProjectUrl"      "<projectUrl>https?://.+</projectUrl>"            "$$nuspec" ; \
	require "RepositoryType"         "<repository[^>]+type=\"git\""                    "$$nuspec" ; \
	require "RepositoryUrl"          "<repository[^>]+url=\"https?://.+\""             "$$nuspec" ; \
	require "PackageReadmeFile"      "<readme>README.md</readme>"                      "$$nuspec" ; \
	require "readme in the package"  "^README.md$$"                                    "${DRY_RUN_DIR}/nupkg.list" ; \
	require "assembly in the package" "^lib/.+/${OndewoPackageId}.dll$$"               "${DRY_RUN_DIR}/nupkg.list" ; \
	require "portable pdb in the symbols" "^lib/.+/${OndewoPackageId}.pdb$$"           "${DRY_RUN_DIR}/snupkg.list" ; \
	if [ $$rc -ne 0 ]; then \
		echo "$(RED)[ERROR]$(NC) ${NUPKG} is missing metadata nuget.org expects - fix ${OndewoPackageId}.csproj" ; \
		exit 1 ; \
	fi ; \
	echo "$(GREEN)[SUCCESS]$(NC) ${NUPKG} and ${SNUPKG} carry the expected metadata"

# The strongest proof available without an API key: resolve the freshly packed .nupkg out of a local
# folder feed into a throwaway consumer project. A malformed nuspec, a wrong id or version, a target
# framework no consumer can use (NU1202) or a dependency that does not exist all fail here - on
# nuget.org the same defects are a rejected upload or a package nobody can install.
#   * --packages points the restore at an EMPTY private packages folder, so the resolution cannot be
#     satisfied from a previous run left in ~/.nuget/packages and pass without touching the feed.
#   * restore only, never build: a build would emit obj/**/*AssemblyInfo.cs under the repository
#     root, where the library project's default Compile glob would pick it up (CS0579).
verify_nupkg_installs: ## Restore the packed package into a throwaway consumer project
	@test -f "${NUPKG}" || { \
		echo "$(RED)[ERROR]$(NC) ${NUPKG} is missing - run 'make pack' first"; \
		exit 1; \
	}
	@rm -rf ${DRY_RUN_DIR}/consumer ${DRY_RUN_DIR}/packages
	@mkdir -p ${DRY_RUN_DIR}/consumer
	@printf '%s\n' \
		'<?xml version="1.0" encoding="utf-8"?>' \
		'<configuration>' \
		'  <packageSources>' \
		'    <clear />' \
		'    <add key="dry-run-local" value="${CURDIR}/${NUPKG_DIR}" />' \
		'    <add key="nuget.org" value="${NUGET_SOURCE}" />' \
		'  </packageSources>' \
		'</configuration>' > ${DRY_RUN_DIR}/consumer/nuget.config
	@printf '%s\n' \
		'<Project Sdk="Microsoft.NET.Sdk">' \
		'  <PropertyGroup>' \
		'    <TargetFramework>netstandard2.0</TargetFramework>' \
		'  </PropertyGroup>' \
		'  <ItemGroup>' \
		'    <PackageReference Include="${OndewoPackageId}" Version="${ONDEWO_VTSI_VERSION}" />' \
		'  </ItemGroup>' \
		'</Project>' > ${DRY_RUN_DIR}/consumer/consumer.csproj
	@echo "$(BLUE)[INFO]$(NC) Restoring ${OndewoPackageId} ${ONDEWO_VTSI_VERSION} from ${CURDIR}/${NUPKG_DIR} into a throwaway consumer ..."
	dotnet restore ${DRY_RUN_DIR}/consumer/consumer.csproj \
		--configfile ${CURDIR}/${DRY_RUN_DIR}/consumer/nuget.config \
		--packages ${CURDIR}/${DRY_RUN_DIR}/packages
	@test -d "${DRY_RUN_DIR}/packages/$$(echo ${OndewoPackageId} | tr 'A-Z' 'a-z')/${ONDEWO_VTSI_VERSION}" || { \
		echo "$(RED)[ERROR]$(NC) the restore succeeded but did not install ${OndewoPackageId} ${ONDEWO_VTSI_VERSION}"; \
		exit 1; \
	}
	@echo "$(GREEN)[SUCCESS]$(NC) a consumer can install ${OndewoPackageId} ${ONDEWO_VTSI_VERSION} and its dependency graph"

# An SDK older than 10.0.400 ignores NUGET_API_KEY and would push WITHOUT a key (see push_to_nuget).
# `release` runs this before anything is committed; push_to_nuget runs it again.
check_nuget_push_sdk: ## Assert this .NET SDK's `dotnet nuget push` reads NUGET_API_KEY from the environment
	@dotnet nuget push --help 2>/dev/null | grep -q NUGET_API_KEY || { \
		echo "$(RED)[ERROR]$(NC) this .NET SDK's 'dotnet nuget push' cannot read NUGET_API_KEY from the"; \
		echo "        environment (SDK 10.0.400 and later can) - install a current .NET 10 SDK"; \
		exit 1; \
	}
	@echo "$(GREEN)[SUCCESS]$(NC) 'dotnet nuget push' reads NUGET_API_KEY from the environment"

# The API key never reaches an argv. `dotnet nuget push` reads it from the NUGET_API_KEY environment
# variable when --api-key is not given (this target is one of NUGET_CREDENTIAL_TARGETS, the only
# ones the key is exported to), so neither the shell nor the NuGet.CommandLine.XPlat.dll the `dotnet`
# muxer re-execs ever carries it on its command line, which /proc/<pid>/cmdline shows to every user
# of the machine. That fallback exists from .NET SDK 10.0.400 on (`dotnet nuget push --help`
# documents it); an older SDK would silently push WITHOUT a key, hence the guard below. A
# NuGet.Config <apikeys> entry is no alternative: on Linux it fails with "Encryption is not
# supported on non-Windows platforms" (measured, SDK 10.0.401). Keep the key narrowly scoped anyway
# (Push only, glob Ondewo.*).
# No --skip-duplicate: release_preflight proved the version is not on nuget.org, so "already exists"
# here is a real problem and fails the release instead of passing as a no-op.
# Pushing the .nupkg also uploads the .snupkg sitting beside it, with the same key.
push_to_nuget: ## Publish the packed NuGet package to nuget.org
	@if [ -z "$${NUGET_API_KEY}" ] || [ "$${NUGET_API_KEY}" = "ENTER_HERE_YOUR_NUGET_API_KEY" ]; then \
		echo "$(RED)[ERROR]$(NC) NUGET_API_KEY is not set - create one at https://www.nuget.org/account/apikeys"; \
		echo "        and add it to ondewo-devops-accounts/account_nuget.env, or export it in the"; \
		echo "        environment (never as a make argument: that is readable in the process table)"; \
		exit 1; \
	fi
	@test -f "${NUPKG}" || { \
		echo "$(RED)[ERROR]$(NC) ${NUPKG} is missing - run 'make build' or 'make pack' first"; \
		exit 1; \
	}
	@test -f "${SNUPKG}" || { \
		echo "$(RED)[ERROR]$(NC) ${SNUPKG} is missing - it is published together with ${NUPKG}"; \
		exit 1; \
	}
	@echo "$(BLUE)[INFO]$(NC) Pushing ${OndewoPackageId} ${ONDEWO_VTSI_VERSION} (+ symbols) to ${NUGET_SOURCE} ..."
	@dotnet nuget push --help 2>/dev/null | grep -q NUGET_API_KEY || { \
		echo "$(RED)[ERROR]$(NC) this .NET SDK's 'dotnet nuget push' cannot read NUGET_API_KEY from the"; \
		echo "        environment (SDK 10.0.400 and later can) - install a current .NET 10 SDK"; \
		exit 1; \
	}
	@dotnet nuget push "${NUPKG}" \
		--source ${NUGET_SOURCE}
	@echo "$(GREEN)[SUCCESS]$(NC) Released to NuGet"

# One look, read-only: true when the flat container's version index lists the version AND its .nupkg
# downloads (200). A shell snippet rather than a sub-make, so the polling loop below does not print
# a make error for every look that finds nothing yet.
NUGET_SERVES_VERSION=listed=no ; served=no ; \
	if curl -fsS --connect-timeout 20 --max-time 60 "${NUGET_FLATCONTAINER}/index.json" 2>/dev/null \
		| grep -Fq '"${ONDEWO_VTSI_VERSION}"' ; then listed=yes ; fi ; \
	code=$$(curl -sS --connect-timeout 20 --max-time 120 -o /dev/null -w '%{http_code}' \
		"${NUGET_FLATCONTAINER}/${ONDEWO_VTSI_VERSION}/$$(echo ${OndewoPackageId} | tr 'A-Z' 'a-z').${ONDEWO_VTSI_VERSION}.nupkg") \
		|| code="no answer" ; \
	if [ "$$code" = 200 ]; then served=yes ; fi ; \
	echo "$(BLUE)[INFO]$(NC) nuget.org: ${ONDEWO_VTSI_VERSION} listed=$$listed, .nupkg HTTP $$code" ; \
	[ "$$listed" = yes ] && [ "$$served" = yes ]
unexport NUGET_SERVES_VERSION

# release.yml uses it to see that `make release` already pushed the version.
check_nuget_serves_version: ## Exit 0 if nuget.org lists ONDEWO_VTSI_VERSION and serves its .nupkg (one look, read-only)
	@$(NUGET_SERVES_VERSION)

# Read-only. A fresh upload is validated and indexed before nuget.org serves it, so this polls
# (NUGET_VERIFY_ATTEMPTS x NUGET_VERIFY_INTERVAL_S, 15 minutes by default) and fails the release when
# the version never appears - the GitHub release, the branch and the tag are never created then.
verify_nuget_published: ## Wait until nuget.org lists ONDEWO_VTSI_VERSION and serves its .nupkg (read-only)
	@attempt=1 ; \
	until { $(NUGET_SERVES_VERSION) ; } ; do \
		if [ $$attempt -ge ${NUGET_VERIFY_ATTEMPTS} ]; then \
			echo "$(RED)[ERROR]$(NC) nuget.org does not serve ${OndewoPackageId} ${ONDEWO_VTSI_VERSION} after ${NUGET_VERIFY_ATTEMPTS} attempts" ; \
			exit 1 ; \
		fi ; \
		echo "$(BLUE)[INFO]$(NC) attempt $$attempt/${NUGET_VERIFY_ATTEMPTS}: not served yet, retrying in ${NUGET_VERIFY_INTERVAL_S} s ..." ; \
		attempt=$$((attempt + 1)) ; \
		sleep ${NUGET_VERIFY_INTERVAL_S} ; \
	done ; \
	echo "$(GREEN)[SUCCESS]$(NC) nuget.org serves ${OndewoPackageId} ${ONDEWO_VTSI_VERSION}"

########################################################
#		DEVOPS-ACCOUNTS

ondewo_release: spc run_release_with_devops ## Release with credentials from devops-accounts repo

# Reads the two credentials and hands them to the release through its ENVIRONMENT (`make release
# NAME=value` would put them on make's argv). The grep is anchored (`^NAME=`), so a comment that
# merely mentions a name cannot match.
#   * DEVOPS_ACCOUNT_DIR set to an existing directory (the ondewo-vtsi-api driver's one clone for all
#     eleven clients): read from there, and leave the directory to its owner.
#   * otherwise: clone into a fresh directory OUTSIDE this repository - the repository is mounted
#     into the compiler and dotnet containers - and DELETE it right after the credentials are read,
#     before the release starts. The trap deletes it on every other way out (a failed clone or read,
#     Ctrl-C), so no account file outlives this recipe.
run_release_with_devops: ## Read credentials from devops-accounts, delete the clone, then run the full release
	@set -eu ; \
	dir="$${DEVOPS_ACCOUNT_DIR:-}" ; own="" ; \
	if [ -z "$$dir" ] || [ ! -d "$$dir" ]; then \
		own=$$(mktemp -d "$${TMPDIR:-/tmp}/${DEVOPS_ACCOUNT_GIT}.XXXXXX") ; \
		trap 'rm -rf "$$own"' EXIT INT TERM ; \
		git clone --quiet --depth 1 git@bitbucket.org:ondewo/${DEVOPS_ACCOUNT_GIT}.git "$$own" ; \
		dir="$$own" ; \
	fi ; \
	set -a ; \
	eval "$$(grep -h -E '^(GITHUB_GH_TOKEN|NUGET_API_KEY)=' "$$dir/account_github.env" "$$dir/account_nuget.env")" ; \
	set +a ; \
	if [ -n "$$own" ]; then rm -rf "$$own" ; fi ; \
	$(MAKE) release

spc: ## Checks if the Release Branch and Tag already exist
	$(eval filtered_branches:= $(shell git branch --all | grep -E "(^|[ /])release/$(subst .,\.,${ONDEWO_VTSI_VERSION})$$"))
	$(eval filtered_tags:= $(shell git tag --list | grep -Fx "${ONDEWO_VTSI_VERSION}"))
	@if test "$(filtered_branches)" != ""; then echo "-- Test 1: Branch exists!!" && exit 1; else echo "-- Test 1: Branch is fine";fi
	@if test "$(filtered_tags)" != ""; then echo "-- Test 2: Tag exists!!" && exit 1; else echo "-- Test 2: Tag is fine";fi
