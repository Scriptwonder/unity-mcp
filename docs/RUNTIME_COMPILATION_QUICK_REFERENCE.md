# Runtime Compilation Quick Reference

## Available Functions

### 1. compile_runtime_code() - Direct Assembly Control
**Use when**: You need low-level assembly management

```python
await compile_runtime_code(
    code="using UnityEngine; public class Test : MonoBehaviour {}",
    assembly_name="MyScripts_001",
    attach_to_gameobject="Player",
    load_immediately=True
)
```

**Returns**: Assembly info, types, attachment status, DLL path

---

### 2. execute_with_roslyn() - Advanced Execution (RECOMMENDED)
**Use when**: You want MonoBehaviour support, coroutines, and history tracking

```python
await execute_with_roslyn(
    code="using UnityEngine; public class Rotator : MonoBehaviour { void Update() { transform.Rotate(Vector3.up * 30f * Time.deltaTime); } }",
    class_name="Rotator",
    target_object="Cube",
    attach_as_component=True
)
```

**Features**:
- ✅ Auto-detects MonoBehaviours
- ✅ Coroutine support
- ✅ History tracking
- ✅ GUI integration

---

### 3. list_loaded_assemblies() - Query Assemblies
```python
result = await list_loaded_assemblies()
# Shows all dynamic assemblies with types and timestamps
```

---

### 4. get_assembly_types() - Inspect Assembly
```python
result = await get_assembly_types(assembly_name="MyScripts_001")
# Shows all types with MonoBehaviour status, base types, etc.
```

---

### 5. get_compilation_history() - View History
```python
result = await get_compilation_history()
# Shows all compilations with timestamps, success status, diagnostics
```

---

### 6. save_compilation_history() - Export History
```python
result = await save_compilation_history()
# Saves to: ProjectRoot/RoslynHistory/RoslynHistory_TIMESTAMP.json
```

---

### 7. clear_compilation_history() - Reset History
```python
result = await clear_compilation_history()
# Clears in-memory history (saved files remain)
```

---

## Common Patterns

### Pattern 1: Simple MonoBehaviour
```python
result = await execute_with_roslyn(
    code="""
    using UnityEngine;
    public class SimpleScript : MonoBehaviour {
        void Start() { Debug.Log("Hello!"); }
    }
    """,
    class_name="SimpleScript",
    target_object="GameObject",
    attach_as_component=True
)
```

### Pattern 2: Static Utility Method
```python
result = await execute_with_roslyn(
    code="""
    using UnityEngine;
    public class AIGenerated {
        public static void Run(GameObject host) {
            Debug.Log($"Running on {host.name}");
            host.transform.position += Vector3.up;
        }
    }
    """,
    class_name="AIGenerated",
    method_name="Run",
    target_object="Player",
    attach_as_component=False
)
```

### Pattern 3: Coroutine
```python
result = await execute_with_roslyn(
    code="""
    using UnityEngine;
    using System.Collections;
    public class AIGenerated {
        public static IEnumerator RunCoroutine(MonoBehaviour host) {
            for (int i = 0; i < 5; i++) {
                Debug.Log($"Step {i}");
                yield return new WaitForSeconds(1f);
            }
        }
    }
    """,
    class_name="AIGenerated",
    method_name="RunCoroutine"
)
```

### Pattern 4: Compile Multiple, Execute Later
```python
# Compile without executing
result1 = await compile_runtime_code(
    code="using UnityEngine; public class Script1 : MonoBehaviour {}",
    assembly_name="Scripts_001",
    load_immediately=True
)

result2 = await compile_runtime_code(
    code="using UnityEngine; public class Script2 : MonoBehaviour {}",
    assembly_name="Scripts_002", 
    load_immediately=True
)

# List what's loaded
assemblies = await list_loaded_assemblies()

# Execute specific one
result = await execute_with_roslyn(
    code="using UnityEngine; public class Script1 : MonoBehaviour { void Start() { Debug.Log(\"Script1 executing!\"); } }",
    class_name="Script1",
    target_object="Player",
    attach_as_component=True
)
```

## Decision Tree

```
Need to compile C# code?
│
├─ Do you need MonoBehaviour attachment?
│  │
│  ├─ YES → Use execute_with_roslyn() with attach_as_component=True
│  │
│  └─ NO → Continue...
│
├─ Do you need coroutine support?
│  │
│  ├─ YES → Use execute_with_roslyn()
│  │
│  └─ NO → Continue...
│
├─ Do you want compilation history?
│  │
│  ├─ YES → Use execute_with_roslyn()
│  │
│  └─ NO → Continue...
│
├─ Do you need to query assemblies later?
│  │
│  ├─ YES → Use compile_runtime_code() + list_loaded_assemblies()
│  │
│  └─ NO → Use execute_with_roslyn() (simplest)
│
└─ Do you need low-level control?
   │
   ├─ YES → Use compile_runtime_code()
   │
   └─ NO → Use execute_with_roslyn() (recommended)
```

## GUI Tool Access

Open: **Window > Roslyn Runtime Compiler**

**Compiler Tab**: Edit and compile code manually
**History Tab**: View all compilations (MCP + GUI)

**Workflow**:
1. Compile via MCP using `execute_with_roslyn()`
2. Open GUI window (Window > Roslyn Runtime Compiler)
3. Switch to History tab
4. See your MCP compilation
5. Click "Load to Compiler" to edit
6. Modify and test in GUI
7. Save as .cs file or copy back to MCP

## Error Handling

### Compilation Errors
```python
result = await execute_with_roslyn(code="invalid code")
if not result["success"]:
    for error in result["data"]["errors"]:
        print(f"Line {error['line']}: {error['message']}")
```

### GameObject Not Found
```python
result = await execute_with_roslyn(
    code=code,
    target_object="NonExistentObject"
)
if not result["success"]:
    print(f"Error: {result['message']}")
```

### No Roslyn Installed
```python
result = await compile_runtime_code(code=code)
if not result["success"] and "Roslyn" in result["message"]:
    print("Install Roslyn: Microsoft.CodeAnalysis.CSharp NuGet package")
    print("Add USE_ROSLYN to Scripting Define Symbols")
```

## Best Practices

✅ **DO**:
- Use `execute_with_roslyn()` for most cases (simplest)
- Set `attach_as_component=True` for MonoBehaviours
- Review history periodically: `get_compilation_history()`
- Export history: `save_compilation_history()`
- Test complex code in GUI first

❌ **DON'T**:
- Reuse assembly names (causes conflicts)
- Compile large files (split into smaller classes)
- Forget to check success status
- Ignore compilation errors
- Compile in tight loops (memory leaks)

## Troubleshooting

**"Roslyn not available"**
→ Install Microsoft.CodeAnalysis.CSharp, add USE_ROSLYN define

**"GameObject not found"**
→ Use hierarchical path: "Canvas/Panel/Button"

**"Type not found in assembly"**
→ Check class name matches exactly (case-sensitive)

**"Assembly already exists"**
→ Use unique names or let tool auto-generate with timestamp

**History not persisting**
→ History is in-memory only until saved with `save_compilation_history()`

**GUI not showing MCP entries**
→ Make sure to use `execute_with_roslyn()` (not `compile_runtime_code()`)

## Performance Notes

- ⚡ First compilation: ~1-2 seconds (Roslyn startup)
- ⚡ Subsequent: ~200-500ms (cached references)
- 💾 Memory: ~20MB per assembly (cannot be unloaded)
- 💾 History: ~1KB per entry (JSON export ~100KB for 100 entries)

## Limits

- ⚠️ Assemblies cannot be unloaded (restart Unity to free memory)
- ⚠️ Max ~1000 assemblies before performance degrades
- ⚠️ History grows indefinitely (clear periodically)
- ⚠️ Editor-only (does not work in builds)

## Quick Comparison

| Feature | compile_runtime_code | execute_with_roslyn |
|---------|---------------------|---------------------|
| Complexity | Medium | Simple |
| MonoBehaviour | Manual | Auto |
| Coroutines | No | Yes |
| History | No | Yes |
| GUI Integration | No | Yes |
| Assembly Tracking | Manual | Auto |
| Best For | Low-level control | Most use cases |
