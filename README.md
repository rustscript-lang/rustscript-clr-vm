# RustScript CLR VM

This repository carries the CLR runtime/compiler support split from the original `project-d` history.

## Repository split

- RustScript core VM and standard library: https://github.com/rustscript-lang/rustscript
- CLR VM: https://github.com/rustscript-lang/rustscript-clr-vm
- Edge runtime and ABI: https://github.com/rustscript-lang/pd-edge
- Controller: https://github.com/rustscript-lang/pd-controller

## Contents

The CLR implementation is under `pd-vm-clr/` and includes runtime, compiler, runner, tests, examples, and a minimal edge HTTP runtime.

## Test

```bash
dotnet test pd-vm-clr/pd-vm-clr.sln
```
