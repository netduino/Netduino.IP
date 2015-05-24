//-----------------------------------------------------------------------------
//
//                   ** WARNING! ** 
//    This file was generated automatically by a tool.
//    Re-running the tool will overwrite this file.
//    You should copy this file to a custom location
//    before adding any customization in the copy to
//    prevent loss of your changes when the tool is
//    re-run.
//
//-----------------------------------------------------------------------------


#ifndef _NETDUINO_IP_INTEROP_NETDUINO_IP_INTEROP_NETWORKINTERFACE_H_
#define _NETDUINO_IP_INTEROP_NETDUINO_IP_INTEROP_NETWORKINTERFACE_H_

namespace Netduino
{
    namespace IP
    {
        namespace Interop
        {
            struct NetworkInterface
            {
                // Helper Functions to access fields of managed object
                // Declaration of stubs. These functions are implemented by Interop code developers
                static void InitializeNetworkInterfaceSettings( CLR_RT_HeapBlock* pMngObj, HRESULT &hr );
                static void UpdateConfiguration( CLR_RT_HeapBlock* pMngObj, INT32 param0, HRESULT &hr );
                static INT32 GetNetworkInterfaceCount( HRESULT &hr );
                static UNSUPPORTED_TYPE GetNetworkInterface( UINT32 param0, HRESULT &hr );
                static UINT32 IPAddressFromString( LPCSTR param0, HRESULT &hr );
            };
        }
    }
}
#endif  //_NETDUINO_IP_INTEROP_NETDUINO_IP_INTEROP_NETWORKINTERFACE_H_
