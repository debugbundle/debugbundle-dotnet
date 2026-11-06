DOTNET ?= dotnet
CONFIGURATION ?= Release
FORMAT_ARGS ?= --verify-no-changes
VERSION ?= 2.1.1
SMOKE_TFM ?= net8.0
TEST_PROJECT ?= tests/DebugBundle.Sdk.Tests/DebugBundle.Sdk.Tests.csproj
DOTNET_IMAGE ?= mcr.microsoft.com/dotnet/sdk:8.0
DOTNET_ROLL_FORWARD ?= Minor
DOCKER_RUN = docker run --rm -e DOTNET_ROLL_FORWARD=$(DOTNET_ROLL_FORWARD) -v debugbundle-dotnet-nuget-cache:/root/.nuget/packages -v "$(CURDIR):/workspace" -w /workspace $(DOTNET_IMAGE)

.PHONY: check-docker test-focused-docker format-docker smoke-docker coverage-docker
check-docker:
	$(DOCKER_RUN) sh -lc 'dotnet restore && dotnet build --configuration $(CONFIGURATION) --no-restore && dotnet test --configuration $(CONFIGURATION) --no-build'

test-focused-docker:
	$(DOCKER_RUN) sh -lc 'dotnet test $(TEST_PROJECT) --configuration $(CONFIGURATION) --filter "$(TEST_FILTER)"'

format-docker:
	$(DOCKER_RUN) sh -lc 'dotnet format $(FORMAT_ARGS)'

smoke-docker:
	$(DOCKER_RUN) sh -lc 'dotnet restore && dotnet build --configuration $(CONFIGURATION) --no-restore && dotnet pack --configuration $(CONFIGURATION) --no-build --output artifacts/packages && dotnet restore smoke/clean-install/DebugBundle.Smoke.csproj -p:DebugBundlePackageVersion=$(VERSION) -p:DebugBundleSmokeTargetFramework=$(SMOKE_TFM) --packages /tmp/debugbundle-smoke-packages --source artifacts/packages --source https://api.nuget.org/v3/index.json && dotnet run --project smoke/clean-install/DebugBundle.Smoke.csproj --configuration $(CONFIGURATION) --no-restore -p:DebugBundlePackageVersion=$(VERSION) -p:DebugBundleSmokeTargetFramework=$(SMOKE_TFM)'

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

.PHONY: build-docker
build-docker:
	$(DOCKER_RUN) sh -lc 'dotnet build --configuration $(CONFIGURATION)'

.PHONY: evidence-runtime-matrix audit-docker
evidence-runtime-matrix:
	mkdir -p .tmp/evidence-runtime
	@set -eu; for lane in 8 10; do \
	  tfm=net$$lane.0; image=mcr.microsoft.com/dotnet/sdk:$$lane.0; \
	  $(MAKE) check-docker DOTNET_IMAGE=$$image DOTNET_ROLL_FORWARD=Major > .tmp/evidence-runtime/check-$$lane.log 2>&1 || { tail -n 35 .tmp/evidence-runtime/check-$$lane.log; exit 1; }; \
	  $(MAKE) smoke-docker DOTNET_IMAGE=$$image DOTNET_ROLL_FORWARD=Major SMOKE_TFM=$$tfm > .tmp/evidence-runtime/installed-$$lane.log 2>&1 || { tail -n 35 .tmp/evidence-runtime/installed-$$lane.log; exit 1; }; \
	  echo ".NET $$lane regression/build/clean-install evidence flow passed"; \
	done

audit-docker:
	$(DOCKER_RUN) sh -lc 'dotnet list DebugBundle.DotNet.sln package --vulnerable --include-transitive'

.PHONY: evidence-performance
evidence-performance:
	$(DOCKER_RUN) sh -lc 'dotnet build perf/DebugBundle.EvidencePerformance.csproj --configuration $(CONFIGURATION) -p:EvidenceTargetFramework=$(SMOKE_TFM)'
	docker run --rm --network none -v "$(CURDIR):/workspace:ro" -w /workspace $(DOTNET_IMAGE) dotnet perf/bin/$(CONFIGURATION)/$(SMOKE_TFM)/DebugBundle.EvidencePerformance.dll

.PHONY: smoke-installed-docker
smoke-installed-docker:
	$(DOCKER_RUN) sh -lc 'dotnet restore smoke/clean-install/DebugBundle.Smoke.csproj -p:DebugBundlePackageVersion=$(VERSION) -p:DebugBundleSmokeTargetFramework=$(SMOKE_TFM) --packages /tmp/debugbundle-smoke-packages --source artifacts/packages --source https://api.nuget.org/v3/index.json && dotnet run --project smoke/clean-install/DebugBundle.Smoke.csproj --configuration $(CONFIGURATION) --no-restore -p:DebugBundlePackageVersion=$(VERSION) -p:DebugBundleSmokeTargetFramework=$(SMOKE_TFM)'
