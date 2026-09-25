DOTNET ?= dotnet
CONFIGURATION ?= Release
FORMAT_ARGS ?= --verify-no-changes
VERSION ?= 2.0.0
SMOKE_TFM ?= net8.0
DOTNET_IMAGE ?= mcr.microsoft.com/dotnet/sdk:8.0
DOCKER_RUN = docker run --rm -v "$(CURDIR):/workspace" -w /workspace $(DOTNET_IMAGE)

.PHONY: check-docker test-focused-docker format-docker smoke-docker coverage-docker
check-docker:
	$(DOCKER_RUN) sh -lc 'dotnet restore && dotnet build --configuration $(CONFIGURATION) --no-restore && dotnet test --configuration $(CONFIGURATION) --no-build'

test-focused-docker:
	$(DOCKER_RUN) sh -lc 'dotnet test tests/DebugBundle.Sdk.Tests/DebugBundle.Sdk.Tests.csproj --configuration $(CONFIGURATION) --filter "$(TEST_FILTER)"'

format-docker:
	$(DOCKER_RUN) sh -lc 'dotnet format --verify-no-changes'

smoke-docker:
	$(DOCKER_RUN) sh -lc 'dotnet restore && dotnet build --configuration $(CONFIGURATION) --no-restore && dotnet pack --configuration $(CONFIGURATION) --no-build --output artifacts/packages && dotnet restore smoke/clean-install/DebugBundle.Smoke.csproj -p:DebugBundlePackageVersion=$(VERSION) -p:DebugBundleSmokeTargetFramework=$(SMOKE_TFM) --source artifacts/packages --source https://api.nuget.org/v3/index.json && dotnet run --project smoke/clean-install/DebugBundle.Smoke.csproj --configuration $(CONFIGURATION) --no-restore -p:DebugBundlePackageVersion=$(VERSION) -p:DebugBundleSmokeTargetFramework=$(SMOKE_TFM)'

coverage-docker:
	rm -rf artifacts/coverage
	$(DOCKER_RUN) sh -lc 'dotnet test DebugBundle.DotNet.sln --configuration $(CONFIGURATION) --collect:"XPlat Code Coverage" --results-directory artifacts/coverage && dotnet run --project tools/DebugBundle.CoverageGate/DebugBundle.CoverageGate.csproj --configuration $(CONFIGURATION) -- artifacts/coverage 80'

.PHONY: restore
restore:
	$(DOTNET) restore

.PHONY: build
build:
	$(DOTNET) build --configuration $(CONFIGURATION) --no-restore

.PHONY: test
test:
	$(DOTNET) test --configuration $(CONFIGURATION) --no-build

.PHONY: coverage
coverage:
	rm -rf artifacts/coverage
	$(DOTNET) test --configuration $(CONFIGURATION) --no-build --collect:"XPlat Code Coverage" --results-directory artifacts/coverage
	$(DOTNET) run --project tools/DebugBundle.CoverageGate/DebugBundle.CoverageGate.csproj --configuration $(CONFIGURATION) -- artifacts/coverage 80

.PHONY: format
format:
	$(DOTNET) format $(FORMAT_ARGS)

.PHONY: pack
pack:
	$(DOTNET) pack --configuration $(CONFIGURATION) --no-build --output artifacts/packages

.PHONY: smoke
smoke: pack
	rm -rf artifacts/smoke
	mkdir -p artifacts/smoke/nuget-cache
	$(DOTNET) restore smoke/clean-install/DebugBundle.Smoke.csproj -p:DebugBundlePackageVersion=$(VERSION) -p:DebugBundleSmokeTargetFramework=$(SMOKE_TFM) --packages artifacts/smoke/nuget-cache --source artifacts/packages --source https://api.nuget.org/v3/index.json
	$(DOTNET) run --project smoke/clean-install/DebugBundle.Smoke.csproj --configuration $(CONFIGURATION) --no-restore -p:DebugBundlePackageVersion=$(VERSION) -p:DebugBundleSmokeTargetFramework=$(SMOKE_TFM)

.PHONY: smoke-published
smoke-published:
	rm -rf artifacts/smoke-published
	mkdir -p artifacts/smoke-published/nuget-cache
	$(DOTNET) restore smoke/clean-install/DebugBundle.Smoke.csproj -p:DebugBundlePackageVersion=$(VERSION) -p:DebugBundleSmokeTargetFramework=$(SMOKE_TFM) --packages artifacts/smoke-published/nuget-cache --source https://api.nuget.org/v3/index.json
	$(DOTNET) run --project smoke/clean-install/DebugBundle.Smoke.csproj --configuration $(CONFIGURATION) --no-restore -p:DebugBundlePackageVersion=$(VERSION) -p:DebugBundleSmokeTargetFramework=$(SMOKE_TFM)

.PHONY: verify
verify: restore build test coverage format pack smoke
