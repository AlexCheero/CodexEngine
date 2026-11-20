"""Prints a Shader Graph's nodes, notable values and edges. python graph_dump.py <file.shadergraph>"""
import json, sys, re
text = open(sys.argv[1], encoding="utf-8").read()
docs, depth, start = [], 0, None
for i, ch in enumerate(text):
    if ch == "{":
        if depth == 0: start = i
        depth += 1
    elif ch == "}":
        depth -= 1
        if depth == 0: docs.append(json.loads(text[start:i + 1]))
by = {d["m_ObjectId"]: d for d in docs if "m_ObjectId" in d}
graph = next(d for d in docs if d.get("m_Type", "").endswith("GraphData"))
def slot_name(ref):
    s = by.get(ref["m_Id"] if isinstance(ref, dict) else ref)
    return s
for d in docs:
    t = d.get("m_Type", "")
    if t.endswith("Node"):
        extra = {k: v for k, v in d.items() if k in ("m_Value", "m_Color", "m_SerializedDescriptor", "m_Property", "m_HashType", "m_CellDensity", "m_AngleOffset")}
        slots = []
        for s in d.get("m_Slots", []):
            sd = by.get(s["m_Id"])
            if sd and "m_Value" in sd and not isinstance(sd["m_Value"], dict) or sd and isinstance(sd.get("m_Value"), dict) and set(sd["m_Value"]) <= {"x","y","z","w"}:
                slots.append(f'{sd.get("m_DisplayName")}={sd.get("m_Value")}')
        print(d["m_ObjectId"][:6], t.split(".")[-1], d.get("m_Name", ""), extra, slots)
for e in graph.get("m_Edges", []):
    o, i = e["m_OutputSlot"], e["m_InputSlot"]
    on, inn = by.get(o["m_Node"]["m_Id"], {}), by.get(i["m_Node"]["m_Id"], {})
    print("EDGE", on.get("m_Name"), o["m_SlotId"], "->", inn.get("m_Name"), i["m_SlotId"])
