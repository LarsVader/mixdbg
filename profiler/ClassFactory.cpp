// ClassFactory.cpp — COM class factory for MixDbgProfiler.

#include "ClassFactory.h"
#include "MixDbgProfiler.h"

ClassFactory::ClassFactory() : m_refCount(1) {}

HRESULT STDMETHODCALLTYPE ClassFactory::QueryInterface(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IClassFactory) {
        *ppv = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

ULONG STDMETHODCALLTYPE ClassFactory::AddRef() { return InterlockedIncrement(&m_refCount); }

ULONG STDMETHODCALLTYPE ClassFactory::Release() {
    LONG ref = InterlockedDecrement(&m_refCount);
    if (ref == 0) delete this;
    return (ULONG)ref;
}

HRESULT STDMETHODCALLTYPE ClassFactory::CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) {
    if (pUnkOuter) return CLASS_E_NOAGGREGATION;
    auto* profiler = new MixDbgProfiler();
    HRESULT hr = profiler->QueryInterface(riid, ppv);
    profiler->Release();
    return hr;
}

HRESULT STDMETHODCALLTYPE ClassFactory::LockServer(BOOL) { return S_OK; }
