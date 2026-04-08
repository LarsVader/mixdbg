CONFIG = Debug

.PHONY: all build profiler testapp test unit integration clean clean-all

all: build profiler testapp

build:
	dotnet build src/MixDbg.csproj -c $(CONFIG)

profiler:
	$(MAKE) -C profiler all

testapp:
	$(MAKE) -C test/TestApp all

test: unit integration

unit:
	dotnet test test/UnitTests/MixDbg.UnitTests.csproj -c $(CONFIG) --no-restore --settings coverage.runsettings

integration:
	dotnet test test/IntegrationTests/MixDbg.IntegrationTests.csproj -c $(CONFIG) --no-restore

clean:
	dotnet clean src/MixDbg.csproj -c $(CONFIG)
	$(MAKE) -C profiler clean
	$(MAKE) -C test/TestApp clean

clean-all: clean
	rm -rf src/bin src/obj
	rm -rf src/MixDbg.EngineWrappers/bin src/MixDbg.EngineWrappers/obj
	rm -rf test/UnitTests/bin test/UnitTests/obj
	rm -rf test/IntegrationTests/bin test/IntegrationTests/obj
