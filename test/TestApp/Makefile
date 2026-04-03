MSBUILD = "c:/Program Files (x86)/Microsoft Visual Studio/18/BuildTools/MSBuild/Current/Bin/MSBuild.exe"
SLN = TestApp.sln
CONFIG = Debug
PLATFORM = x64
OUTPUT = WpfApp/bin/x64/$(CONFIG)/net10.0-windows

.PHONY: all build release restore clean run

all: restore build

restore:
	dotnet restore WpfApp/WpfApp.csproj

build:
	$(MSBUILD) $(SLN) -p:Configuration=$(CONFIG) -p:Platform=$(PLATFORM) -nologo -v:minimal

release:
	$(MSBUILD) $(SLN) -p:Configuration=Release -p:Platform=$(PLATFORM) -nologo -v:minimal

clean:
	$(MSBUILD) $(SLN) -p:Configuration=$(CONFIG) -p:Platform=$(PLATFORM) -t:Clean -nologo -v:minimal
	rm -rf x64 WpfApp/bin WpfApp/obj CliWrapper/x64 NativeLib/x64

run: build
	$(OUTPUT)/WpfApp.exe
