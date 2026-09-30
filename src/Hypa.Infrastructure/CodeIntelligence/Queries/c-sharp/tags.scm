;; Hypa C# tree-sitter query pack (Slice 1 — Deep Code Graph)
;;
;; Acquired base:
;;   - tree-sitter/tree-sitter-c-sharp queries/tags.scm (MIT)
;;     © 2014-2023 Max Brunsfeld, Damien Guard, Amaan Qureshi, and contributors
;;   - nvim-treesitter queries/c_sharp/locals.scm (Apache-2.0)
;;
;; Hypa-authored extensions (not covered by the packs above):
;;   field, constructor, enum + members, struct, record, event,
;;   file-scoped namespace, primary-constructor parameter lists,
;;   type parameters, imports, base-list relationships, calls. Accessibility,
;;   modifiers, parameter lists, and return types are read from captured definition nodes.
;;
;; Node names verified against tree-sitter-c-sharp ABI 15 as bundled by
;; TreeSitter.DotNet 1.3.0. Capture names map to Hypa CodeSymbol.Kind values
;; in TreeSitterQueryExtractor.

; ---- type / member definitions (tags.scm + locals.scm + gaps) ----

(class_declaration
  name: (identifier) @name
  (parameter_list)? @primary.ctor) @definition.class

(interface_declaration
  name: (identifier) @name) @definition.interface

(struct_declaration
  name: (identifier) @name
  (parameter_list)? @primary.ctor) @definition.struct

(enum_declaration
  name: (identifier) @name) @definition.enum

; note: `record struct` is also record_declaration in this grammar (no record_struct_declaration node)
(record_declaration
  name: (identifier) @name
  (parameter_list)? @primary.ctor) @definition.record

(method_declaration
  name: (identifier) @name) @definition.method

(local_function_statement
  name: (identifier) @name) @definition.method

(constructor_declaration
  name: (identifier) @name) @definition.constructor

(property_declaration
  name: (identifier) @name) @definition.property

; multi-declarator fields produce one match per variable_declarator
(field_declaration
  (variable_declaration
    (variable_declarator
      (identifier) @name))) @definition.field

(event_field_declaration
  (variable_declaration
    (variable_declarator
      (identifier) @name))) @definition.event

(event_declaration
  name: (identifier) @name) @definition.event

(enum_member_declaration
  name: (identifier) @name) @definition.enum_member

(namespace_declaration
  name: [
    (identifier) @name
    (qualified_name) @name
  ]) @definition.namespace

(file_scoped_namespace_declaration
  name: [
    (identifier) @name
    (qualified_name) @name
  ]) @definition.namespace

(type_parameter
  (identifier) @name) @definition.type_parameter

; record positional parameters surface as public properties in C#
(record_declaration
  name: (identifier) @owner
  (parameter_list
    (parameter
      name: (identifier) @name))) @definition.record_positional

; ---- base / inheritance lists ----
; Simple, generic, qualified (Namespace.Type / global::System.IDisposable), and
; primary-constructor base forms. Extractor keeps global:: on TargetName for
; Slice 3 exact FQN ranking; same-file lookup still normalizes the leaf name.

(class_declaration
  name: (identifier) @source
  (base_list
    (identifier) @name)) @reference.base

(class_declaration
  name: (identifier) @source
  (base_list
    (qualified_name) @name)) @reference.base

(class_declaration
  name: (identifier) @source
  (base_list
    (alias_qualified_name) @name)) @reference.base

(class_declaration
  name: (identifier) @source
  (base_list
    (generic_name
      (identifier) @name))) @reference.base

(class_declaration
  name: (identifier) @source
  (base_list
    (primary_constructor_base_type
      type: (identifier) @name))) @reference.base

(class_declaration
  name: (identifier) @source
  (base_list
    (primary_constructor_base_type
      type: (qualified_name) @name))) @reference.base

(class_declaration
  name: (identifier) @source
  (base_list
    (primary_constructor_base_type
      type: (generic_name
        (identifier) @name)))) @reference.base

(record_declaration
  name: (identifier) @source
  (base_list
    (identifier) @name)) @reference.base

(record_declaration
  name: (identifier) @source
  (base_list
    (qualified_name) @name)) @reference.base

(record_declaration
  name: (identifier) @source
  (base_list
    (alias_qualified_name) @name)) @reference.base

(record_declaration
  name: (identifier) @source
  (base_list
    (primary_constructor_base_type
      type: (identifier) @name))) @reference.base

(record_declaration
  name: (identifier) @source
  (base_list
    (primary_constructor_base_type
      type: (qualified_name) @name))) @reference.base

(record_declaration
  name: (identifier) @source
  (base_list
    (generic_name
      (identifier) @name))) @reference.base

(struct_declaration
  name: (identifier) @source
  (base_list
    (identifier) @name)) @reference.base

(struct_declaration
  name: (identifier) @source
  (base_list
    (qualified_name) @name)) @reference.base

(struct_declaration
  name: (identifier) @source
  (base_list
    (alias_qualified_name) @name)) @reference.base

(struct_declaration
  name: (identifier) @source
  (base_list
    (generic_name
      (identifier) @name))) @reference.base

(interface_declaration
  name: (identifier) @source
  (base_list
    (identifier) @name)) @reference.base

(interface_declaration
  name: (identifier) @source
  (base_list
    (qualified_name) @name)) @reference.base

(interface_declaration
  name: (identifier) @source
  (base_list
    (alias_qualified_name) @name)) @reference.base

(interface_declaration
  name: (identifier) @source
  (base_list
    (generic_name
      (identifier) @name))) @reference.base

; ---- overrides ----

(method_declaration
  (modifier) @_m
  (#eq? @_m "override")
  name: (identifier) @name) @reference.override

; ---- calls ----
; Capture the full member_access_expression (e.g. Console.WriteLine), not only the
; leaf identifier. Leaf-only capture made Slice 3 treat BCL member calls as simple-name
; lookups and falsely bind them to unrelated project methods with the same name.

(invocation_expression
  function: (member_access_expression) @name) @reference.call

(invocation_expression
  function: (identifier) @name) @reference.call

; ---- imports (alias using: capture the imported name, not the alias) ----

(using_directive
  name: (_)
  (qualified_name) @name) @reference.import

(using_directive
  name: (_)
  (identifier) @name) @reference.import

(using_directive
  !name
  (identifier) @name) @reference.import

(using_directive
  !name
  (qualified_name) @name) @reference.import
