// IMFActivate for the virtual camera media source. The Frame Server CoCreates the registered CLSID,
// stores its attributes on this object and calls ActivateObject to obtain the IMFMediaSource
// (same pattern as VirtualCameraMediaSourceActivate in Microsoft's VirtualCamera sample).
#pragma once
#include "SwipeMediaSource.h"

namespace msv
{
    class SwipeMediaSourceActivate :
        public Microsoft::WRL::RuntimeClass<
            Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
            Microsoft::WRL::ChainInterfaces<IMFActivate, IMFAttributes>>,
        private ModuleObjectCounter
    {
    public:
        HRESULT RuntimeClassInitialize();

        // IMFActivate
        IFACEMETHODIMP ActivateObject(REFIID riid, void** object) override;
        IFACEMETHODIMP ShutdownObject() override;
        IFACEMETHODIMP DetachObject() override;

        // IMFAttributes: delegated to an internal attribute store.
        IFACEMETHODIMP GetItem(REFGUID key, PROPVARIANT* propValue) override { return m_attributes->GetItem(key, propValue); }
        IFACEMETHODIMP GetItemType(REFGUID key, MF_ATTRIBUTE_TYPE* type) override { return m_attributes->GetItemType(key, type); }
        IFACEMETHODIMP CompareItem(REFGUID key, REFPROPVARIANT propValue, BOOL* result) override { return m_attributes->CompareItem(key, propValue, result); }
        IFACEMETHODIMP Compare(IMFAttributes* theirs, MF_ATTRIBUTES_MATCH_TYPE type, BOOL* result) override { return m_attributes->Compare(theirs, type, result); }
        IFACEMETHODIMP GetUINT32(REFGUID key, UINT32* propValue) override { return m_attributes->GetUINT32(key, propValue); }
        IFACEMETHODIMP GetUINT64(REFGUID key, UINT64* propValue) override { return m_attributes->GetUINT64(key, propValue); }
        IFACEMETHODIMP GetDouble(REFGUID key, double* propValue) override { return m_attributes->GetDouble(key, propValue); }
        IFACEMETHODIMP GetGUID(REFGUID key, GUID* propValue) override { return m_attributes->GetGUID(key, propValue); }
        IFACEMETHODIMP GetStringLength(REFGUID key, UINT32* length) override { return m_attributes->GetStringLength(key, length); }
        IFACEMETHODIMP GetString(REFGUID key, LPWSTR propValue, UINT32 size, UINT32* length) override { return m_attributes->GetString(key, propValue, size, length); }
        IFACEMETHODIMP GetAllocatedString(REFGUID key, LPWSTR* propValue, UINT32* length) override { return m_attributes->GetAllocatedString(key, propValue, length); }
        IFACEMETHODIMP GetBlobSize(REFGUID key, UINT32* size) override { return m_attributes->GetBlobSize(key, size); }
        IFACEMETHODIMP GetBlob(REFGUID key, UINT8* buffer, UINT32 size, UINT32* blobSize) override { return m_attributes->GetBlob(key, buffer, size, blobSize); }
        IFACEMETHODIMP GetAllocatedBlob(REFGUID key, UINT8** buffer, UINT32* size) override { return m_attributes->GetAllocatedBlob(key, buffer, size); }
        IFACEMETHODIMP GetUnknown(REFGUID key, REFIID riid, LPVOID* propValue) override { return m_attributes->GetUnknown(key, riid, propValue); }
        IFACEMETHODIMP SetItem(REFGUID key, REFPROPVARIANT propValue) override { return m_attributes->SetItem(key, propValue); }
        IFACEMETHODIMP DeleteItem(REFGUID key) override { return m_attributes->DeleteItem(key); }
        IFACEMETHODIMP DeleteAllItems() override { return m_attributes->DeleteAllItems(); }
        IFACEMETHODIMP SetUINT32(REFGUID key, UINT32 propValue) override { return m_attributes->SetUINT32(key, propValue); }
        IFACEMETHODIMP SetUINT64(REFGUID key, UINT64 propValue) override { return m_attributes->SetUINT64(key, propValue); }
        IFACEMETHODIMP SetDouble(REFGUID key, double propValue) override { return m_attributes->SetDouble(key, propValue); }
        IFACEMETHODIMP SetGUID(REFGUID key, REFGUID propValue) override { return m_attributes->SetGUID(key, propValue); }
        IFACEMETHODIMP SetString(REFGUID key, LPCWSTR propValue) override { return m_attributes->SetString(key, propValue); }
        IFACEMETHODIMP SetBlob(REFGUID key, const UINT8* buffer, UINT32 size) override { return m_attributes->SetBlob(key, buffer, size); }
        IFACEMETHODIMP SetUnknown(REFGUID key, IUnknown* propValue) override { return m_attributes->SetUnknown(key, propValue); }
        IFACEMETHODIMP LockStore() override { return m_attributes->LockStore(); }
        IFACEMETHODIMP UnlockStore() override { return m_attributes->UnlockStore(); }
        IFACEMETHODIMP GetCount(UINT32* count) override { return m_attributes->GetCount(count); }
        IFACEMETHODIMP GetItemByIndex(UINT32 index, GUID* key, PROPVARIANT* propValue) override { return m_attributes->GetItemByIndex(index, key, propValue); }
        IFACEMETHODIMP CopyAllItems(IMFAttributes* destination) override { return m_attributes->CopyAllItems(destination); }

    private:
        std::mutex m_lock;
        Microsoft::WRL::ComPtr<IMFAttributes> m_attributes;
        Microsoft::WRL::ComPtr<SwipeMediaSource> m_source;
    };

    class SwipeMediaSourceFactory :
        public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, IClassFactory>,
        private ModuleObjectCounter
    {
    public:
        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** object) override;
        IFACEMETHODIMP LockServer(BOOL lock) override;
    };
}
