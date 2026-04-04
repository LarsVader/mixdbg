MSBUILD = "c:/Program Files (x86)/Microsoft Visual Studio/18/BuildTools/MSBuild/Current/Bin/MSBuild.exe"
CONFIG = Debug
PLATFORM = x64

.PHONY: all clean

all:
	$(MSBUILD) MixDbgProfiler.vcxproj -p:Configuration=$(CONFIG) -p:Platform=$(PLATFORM) -nologo -v:minimal

clean:
	$(MSBUILD) MixDbgProfiler.vcxproj -p:Configuration=$(CONFIG) -p:Platform=$(PLATFORM) -t:Clean -nologo -v:minimal
	rm -rf x64
