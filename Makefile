DOTNET ?= dotnet
CONFIGURATION ?= Release
VERSION ?= 1.1.0
SMOKE_TFM ?= net8.0

.PHONY: restore
restore:
	$(DOTNET) restore

.PHONY: build
build:
	$(DOTNET) build --configuration $(CONFIGURATION) --no-restore

.PHONY: test
test:
	$(DOTNET) test --configuration $(CONFIGURATION) --no-build

.PHONY: format
format:
	$(DOTNET) format --verify-no-changes

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
verify: restore build test format pack smoke
