from idautils import *
from idaapi import *
from idc import *
import ida_bytes
import traceback
import re
import json

DEMANGLER = 3  # MSVC

xref_data = {}

def clean_demangled(name: str) -> str:
    first_colon = name.find("::")
    if first_colon != -1:
        prefix = name[:first_colon]
        last_space = prefix.rfind(" ")
        if last_space != -1:
            name = name[last_space + 1:]

    if re.search(r'TryGet.*Array', name):
        name = name.replace("&", "[]&")

    name = re.sub(r'\s+', '', name)
    return name

for func_ea in Functions():
    try:
        if not ida_bytes.is_code(ida_bytes.get_full_flags(func_ea)):
            continue

        mangled_name = get_name(func_ea)
        if not mangled_name or "::" in mangled_name:
            continue

        demangled = demangle_name(mangled_name, DEMANGLER)
        if demangled is None:
            continue

        demangled = clean_demangled(demangled)

        # Count code xrefs and track callers
        callers = []
        for xref in XrefsTo(func_ea, 0):
            if ida_bytes.is_code(ida_bytes.get_full_flags(xref.frm)):
                caller_name = get_func_name(xref.frm)
                caller_demangled = demangle_name(caller_name, DEMANGLER)
                if caller_demangled:
                    caller_cleaned = clean_demangled(caller_demangled)
                    callers.append(caller_cleaned)

        # skip the following namespaces. they arent used in assembly-csharp in my case
        # you might want to adjust this based on your specific needs
        if any(demangled.startswith(p) for p in (
            "UnityEngine::", "Unity::", "UnityEngineInternal::", "Microsoft::VisualBasic",
            "System::", "Mono::", "MS::Internal", "Microsoft::Win32", "Interop::",
            "Epic::", "Sentry::", "Steamworks::", "Newtonsoft::Json:", "TMPro::",
            "Epic::OnlineServices", "PolyfillExtensions::", "Microsoft::CSharp",
            "Internal::", "Interop_SspiCli::", "std::", "namespace'::")):
            continue
        
        # skip functions that almost definitely arent in assembly-csharp
        if any(p in demangled for p in ("__fastcall","__cdecl","__crt","stdcall","_lambda_","_expandlocale_")):
            continue

        xref_data[demangled] = {
            "CallCount": len(callers),
            "Usages": sorted(callers)
        }

    except Exception as e:
        print(f"Error on function at {hex(func_ea)}: {e}")
        traceback.print_exc()

# Write to JSON
with open("xref_data.json", "w", encoding="utf-8") as f:
    json.dump(xref_data, f, indent=2, ensure_ascii=False)
