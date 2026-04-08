// DllExports.cpp — DLL entry points called by the CLR to instantiate the profiler.
//
// DllGetClassObject and DllCanUnloadNow are already declared in combaseapi.h;
// we just provide the implementations here. The .def file marks them as exports.

#include "ClassFactory.h"
#include "CoreClrTypes.h"

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv) {
    if (rclsid != CLSID_MixDbgProfiler)
        return CLASS_E_CLASSNOTAVAILABLE;

    auto* factory = new ClassFactory();
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow() {
    return S_FALSE; // Never unload — profiler lifetime matches process lifetime.
}
