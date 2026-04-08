// ClassFactory.h — COM class factory for MixDbgProfiler.

#pragma once

#include "CoreClrTypes.h"

class ClassFactory : public IClassFactory {
    volatile LONG m_refCount;

public:
    ClassFactory();

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override;
    ULONG STDMETHODCALLTYPE AddRef() override;
    ULONG STDMETHODCALLTYPE Release() override;
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override;
    HRESULT STDMETHODCALLTYPE LockServer(BOOL) override;
};
