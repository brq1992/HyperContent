# HyperContent Dependencies Check — Todo Test Cases (Revised)

Test cases for verifying that Update Build correctly handles all dependency scenarios.
Each case should be verified end-to-end: Full Build → Update Build → Runtime load.

---

## How to Use

For each test case:
1. Set up the Full Build with the described bundle layout
2. Make the described changes
3. Run Update Build
4. Verify the expected catalog output
5. Run the Runtime load steps and verify no errors

Status column: `[ ]` = not tested, `[x]` = passed, `[!]` = failed

变更标记约定：`*` = 已修改，`+` = 新增，`-` = 已删除

---

## Category 1: 同Bundle，无依赖关系

A和B打在同一个bundle里，彼此没有引用关系。

---

### Case 1.1 — 一个资产变更

**Setup (Full Build)**
```
bundle_ui (Local): A, B
```

**Change**
```
B modified
```

**Update Build output**
```
bundle_ui (Local): A
bundle_ui_update: B*
Catalog:
  A → bundle_ui (Local)
  B → bundle_ui_update (Remote)
```

**Runtime verification**
```
LoadAsync(A) → bundle_ui_update → correct A
LoadAsync(B) → bundle_ui_update → new B
bundle_ui (Local) must NOT be loaded
```

**Status**: `[ ]`

---

### Case 1.2 — 多个资产同时变更

**Setup (Full Build)**
```
bundle_ui (Local): A, B
```

**Change**
```
A modified
B modified
```

**Expected Update Build output**
```
bundle_ui_update: A*, B*
Catalog:
  A → bundle_ui_update (Remote)
  B → bundle_ui_update (Remote)
```

**Runtime verification**
```
LoadAsync(A) → bundle_ui_update → new A
LoadAsync(B) → bundle_ui_update → new B
bundle_ui (Local) must NOT be loaded
```

**Status**: `[ ]`

---

### Case 1.3 — 新增资产

**Setup (Full Build)**
```
bundle_ui (Local): A, B
```

**Change**
```
New asset C added, assigned to bundle_ui by grouping strategy
```

**Update Build output**
```
bundle_ui_update: C+
Catalog:
  A → bundle_ui (Local)
  B → bundle_ui (Local)
  C → bundle_ui_update (Remote)
```

**Runtime verification**
```
LoadAsync(A) → bundle_ui (Local) → correct A
LoadAsync(C) → bundle_ui_update → correct C
```

**Status**: `[ ]`

---

## Category 2: 同Bundle，有依赖关系

A和B打在同一个bundle里，且A引用B（或B引用A）。

---

### Case 2.1 — 被依赖资产变更（A depends on B，B变）

**Setup (Full Build)**
```
bundle_ui (Local): A, B
A depends on B
```

**Change**
```
B modified
```

**Update Build output**
```
bundle_ui_update: A, B*
Catalog:
  A → bundle_ui_update (Remote)
  B → bundle_ui_update (Remote)
```

**Runtime verification**
```
LoadAsync(A) → bundle_ui_update → A gets new B correctly (same bundle, no cross-bundle resolve needed)
bundle_ui (Local) must NOT be loaded
```

**Status**: `[ ]`

---

### Case 2.2 — 依赖方资产变更（A depends on B，A变）

> **A3b verification target**: ShouldSkipStaticDependencyEntry must skip B (same bundle, bundle name unchanged after excluding A). Expect log: `A3b skip: dep entry <B_guid> stays in unchanged bundle 'bundle_ui'`. Expanded set should be {A} only, not {A,B}.

**Setup (Full Build)**
```
bundle_ui (Local): A, B
A depends on B
```

**Change**
```
A modified
```

**Update Build output**
```
bundle_ui (Local): B
bundle_ui_update: A*
Catalog:
  A → bundle_ui_update (Remote)
  B → bundle_ui (Local)
```

**Runtime verification**
```
LoadAsync(A) → bundle_ui_update → new A, gets correct B (same bundle)
bundle_ui (Local) must NOT be loaded
```

**Status**: `[ ]`

---

### Case 2.3 — 双方同时变更（A depends on B，A和B都变）

**Setup (Full Build)**
```
bundle_ui (Local): A, B
A depends on B
```

**Change**
```
A modified
B modified
```

**Update Build output**
```
bundle_ui_update: A*, B*
Catalog:
  A → bundle_ui_update (Remote)
  B → bundle_ui_update (Remote)
```

**Runtime verification**
```
LoadAsync(A) → bundle_ui_update → new A gets new B
bundle_ui (Local) must NOT be loaded
```

**Status**: `[ ]`

---

## Category 3: 不同Bundle，有依赖关系

A和B分别在不同bundle，A引用B。

---

### Case 3.1 — 被依赖资产变更（B变，A不变）

**Setup (Full Build)**
```
bundle_ui (Local): A
bundle_shared (Local): B
A depends on B
```

**Change**
```
B modified
```

**Update Build output**
```
bundle_ui_update: A
bundle_shared_update: B*
Catalog:
  A → bundle_ui_update (Remote)
  B → bundle_shared_update (Remote)
```

**Runtime verification**
```
LoadAsync(A) → bundle_ui (Local) → catalog resolves B → bundle_shared_update → new B
bundle_shared (Local) must NOT be loaded
No duplicate B in memory
```

**Status**: `[ ]`

---

### Case 3.2 — 依赖方资产变更（A变，B不变）

> **A3b verification target**: ShouldSkipStaticDependencyEntry must skip B (different bundle, bundle_shared has no explicit mods, bundle name unchanged). Expect log: `A3b skip: dep entry <B_guid> stays in unchanged bundle 'bundle_shared'`. Expanded set should be {A} only, not {A,B}.

**Setup (Full Build)**
```
bundle_ui (Local): A
bundle_shared (Local): B
A depends on B
```

**Change**
```
A modified
```

**Update Build output**
```
bundle_ui_update: A*
bundle_shared (Local): B
Catalog:
  A → bundle_ui_update (Remote)
  B → bundle_shared (Local)
```

**Runtime verification**
```
LoadAsync(A) → bundle_ui_update → catalog resolves B → bundle_shared (Local) → correct B
```

**Status**: `[ ]`

---

### Case 3.3 — 双方同时变更（A和B都变）

**Setup (Full Build)**
```
bundle_ui (Local): A
bundle_shared (Local): B
A depends on B
```

**Change**
```
A modified
B modified
```

**Update Build output**
```
bundle_ui_update: A*
bundle_shared_update: B*
Catalog:
  A → bundle_ui_update (Remote)
  B → bundle_shared_update (Remote)
```

**Runtime verification**
```
LoadAsync(A) → bundle_ui_update → catalog resolves B → bundle_shared_update → new B
No stale B loaded
```

**Status**: `[ ]`

---

## Category 4: 菱形依赖（A→B←C）

A和C分别依赖同一个B，三者分属不同bundle。

---

### Case 4.1 — 共同依赖变更（B变，A和C不变）

**Setup (Full Build)**
```
bundle_a (Local): A
bundle_shared (Local): B
bundle_c (Local): C
A depends on B, C depends on B
```

**Change**
```
B modified
```

**Update Build output**
```
bundle_a_update: A
bundle_shared_update: B*
bundle_c_update: C
Catalog:
  A → bundle_a_update (Remote)
  B → bundle_shared_update (Remote)
  C → bundle_c_update (Remote)
```

**Runtime verification**
```
LoadAsync(A) → bundle_a → resolves B → bundle_shared_update → new B
LoadAsync(C) → bundle_c → resolves B → bundle_shared_update → new B (same bundle, no duplicate load)
B loaded exactly once
```

**Status**: `[ ]`

---

### Case 4.2 — 单侧依赖方变更（A变，B和C不变）

> **A3b verification target**: ShouldSkipStaticDependencyEntry must skip B (bundle_shared has no explicit mods). Expanded set should be {A} only.

**Setup (Full Build)**
```
bundle_a (Local): A
bundle_shared (Local): B
bundle_c (Local): C
A depends on B, C depends on B
```

**Change**
```
A modified
```

**Update Build output**
```
bundle_a_update: A*
Catalog:
  A → bundle_a_update (Remote)
  B → bundle_shared (Local)
  C → bundle_c (Local)
```

**Runtime verification**
```
LoadAsync(A) → bundle_a_update → resolves B → bundle_shared (Local) → correct B
LoadAsync(C) → bundle_c (Local) → resolves B → bundle_shared (Local) → correct B
```

**Status**: `[ ]`

---

### Case 4.3 — 三者同时变更

**Setup (Full Build)**
```
bundle_a (Local): A
bundle_shared (Local): B
bundle_c (Local): C
A depends on B, C depends on B
```

**Change**
```
A modified
B modified
C modified
```

**Update Build output**
```
bundle_a_update: A*
bundle_shared_update: B*
bundle_c_update: C*
Catalog:
  A → bundle_a_update (Remote)
  B → bundle_shared_update (Remote)
  C → bundle_c_update (Remote)
```

**Runtime verification**
```
LoadAsync(A) → bundle_a_update → resolves B → bundle_shared_update → new B
LoadAsync(C) → bundle_c_update → resolves B → bundle_shared_update → new B (no duplicate load)
```

**Status**: `[ ]`

---

## Category 5: 链式依赖（A→B→C）

A依赖B，B依赖C，三者分属不同bundle。

---

### Case 5.1 — 链尾变更（C变，A和B不变）

**Setup (Full Build)**
```
bundle_a (Local): A
bundle_b (Local): B
bundle_c (Local): C
A depends on B, B depends on C
```

**Change**
```
C modified
```

**Update Build output**
```
bundle_a_update: A
bundle_b_update: B
bundle_c_update: C*
Catalog:
  A → bundle_a_update (Remote)
  B → bundle_b_update (Remote)
  C → bundle_c_update (Remote)
```

**Runtime verification**
```
LoadAsync(A) → bundle_a → resolves B → bundle_b → resolves C → bundle_c_update → new C
No stale C in memory
```

**Status**: `[ ]`

---

### Case 5.2 — 链中变更（B变，A和C不变）

**Setup (Full Build)**
```
bundle_a (Local): A
bundle_b (Local): B
bundle_c (Local): C
A depends on B, B depends on C
```

**Change**
```
B modified
```

**Update Build output**
```
bundle_a_update: A
bundle_b_update: B*
Catalog:
  A → bundle_a_update (Remote)
  B → bundle_b_update (Remote)
  C → bundle_c (Local)
```

**Runtime verification**
```
LoadAsync(A) → bundle_a → resolves B → bundle_b_update → new B → resolves C → bundle_c (Local) → correct C
```

**Status**: `[ ]`

---

### Case 5.3 — 链头变更（A变，B和C不变）

**Setup (Full Build)**
```
bundle_a (Local): A
bundle_b (Local): B
bundle_c (Local): C
A depends on B, B depends on C
```

**Change**
```
A modified
```

**Update Build output**
```
bundle_a_update: A*
Catalog:
  A → bundle_a_update (Remote)
  B → bundle_b (Local)
  C → bundle_c (Local)
```

**Runtime verification**
```
LoadAsync(A) → bundle_a_update → new A → resolves B → bundle_b → correct B → resolves C → bundle_c → correct C
```

**Status**: `[ ]`

---

### Case 5.4 — 三者同时变更

**Setup (Full Build)**
```
bundle_a (Local): A
bundle_b (Local): B
bundle_c (Local): C
A depends on B, B depends on C
```

**Change**
```
A modified
B modified
C modified
```

**Update Build output**
```
bundle_a_update: A*
bundle_b_update: B*
bundle_c_update: C*
Catalog:
  A → bundle_a_update (Remote)
  B → bundle_b_update (Remote)
  C → bundle_c_update (Remote)
```

**Runtime verification**
```
LoadAsync(A) → bundle_a_update → new A → bundle_b_update → new B → bundle_c_update → new C
Each bundle loaded exactly once, no stale assets
```

**Status**: `[ ]`

---

## Summary Checklist

| Category | Cases | Status |
|----------|-------|--------|
| 1. 同Bundle，无依赖 | 1.1 ~ 1.6 | `[ ]` |
| 2. 同Bundle，有依赖 | 2.1 ~ 2.3 | `[ ]` |
| 3. 不同Bundle，有依赖 | 3.1 ~ 3.3 | `[ ]` |
| 4. 菱形依赖 A→B←C | 4.1 ~ 4.3 | `[ ]` |
| 5. 链式依赖 A→B→C | 5.1 ~ 5.4 | `[ ]` |
