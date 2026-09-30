;; Hypa TypeScript / TSX tree-sitter query pack (Slice 4 — Deep Code Graph)
;;
;; Acquired base:
;;   - tree-sitter/tree-sitter-typescript queries/tags.scm (MIT)
;;     © Max Brunsfeld and contributors
;;   - tree-sitter/tree-sitter-javascript queries/tags.scm (MIT)
;;     © Max Brunsfeld and contributors
;;   - nvim-treesitter queries/typescript/locals.scm (Apache-2.0)
;;
;; Hypa-authored extensions (not covered by the packs above):
;;   class/abstract class, type alias, enum + members, fields, constructors
;;   (via method_definition name), interface property/method signatures,
;;   namespace/module, arrow/const function bindings, type parameters,
;;   extends/implements heritage, override, imports, calls.
;;
;; Shared by language ids "typescript" and "tsx" (node types align for both
;; TreeSitter.DotNet grammars). Capture names map to Hypa CodeSymbol.Kind
;; values in TreeSitterQueryExtractor.
;;
;; Deliberately NO identifier-reference firehose.

; ---- type / member definitions ----

(class_declaration
  name: (type_identifier) @name) @definition.class

(abstract_class_declaration
  name: (type_identifier) @name) @definition.class

(interface_declaration
  name: (type_identifier) @name) @definition.interface

(type_alias_declaration
  name: (type_identifier) @name) @definition.type_alias

(enum_declaration
  name: (identifier) @name) @definition.enum

(enum_assignment
  name: (property_identifier) @name) @definition.enum_member

; Bare members: enum Color { Red, Blue } — name fields on enum_body (not enum_assignment).
; Capture definition on the property_identifier so each member gets a unique DefNode.
(enum_body
  name: (property_identifier) @name @definition.enum_member)

(function_declaration
  name: (identifier) @name) @definition.function

(function_expression
  name: (identifier) @name) @definition.function

(generator_function_declaration
  name: (identifier) @name) @definition.function

(function_signature
  name: (identifier) @name) @definition.function

; methods + constructors (name "constructor") + getters/setters, including #private names
(method_definition
  name: [
    (property_identifier) @name
    (private_property_identifier) @name
  ]) @definition.method

(method_signature
  name: (property_identifier) @name) @definition.method

(abstract_method_signature
  name: (property_identifier) @name) @definition.method

(public_field_definition
  name: [
    (property_identifier) @name
    (private_property_identifier) @name
  ]) @definition.field

(property_signature
  name: (property_identifier) @name) @definition.property

; named arrow / function bindings: const f = () => {} / let g = function() {}
(lexical_declaration
  (variable_declarator
    name: (identifier) @name
    value: [(arrow_function) (function_expression) (generator_function)])) @definition.function

(variable_declaration
  (variable_declarator
    name: (identifier) @name
    value: [(arrow_function) (function_expression) (generator_function)])) @definition.function

(internal_module
  name: (identifier) @name) @definition.namespace

(module
  name: (identifier) @name) @definition.namespace

(type_parameter
  name: (type_identifier) @name) @definition.type_parameter

; local bindings exported later (`const helper = ...; export { helper };`). Direct
; `export` / `export default` declarations are detected from the definition ancestor.
; Re-exports (`export { name } from '…'`) are captured here but filtered in C# when a
; `source` field is present so local same-name symbols stay not-exported.
; `export { x as default }` is detected via the specifier alias field in C#.
(export_statement
  (export_clause
    (export_specifier
      name: (identifier) @export.name))) @export.statement

; Separated default: `class Foo {}; export default Foo;` (not a direct export parent).
(export_statement
  "default"
  (identifier) @export.default)

; ---- heritage ----

(class_declaration
  name: (type_identifier) @source
  (class_heritage
    (extends_clause
      value: [
        (identifier) @name
        (type_identifier) @name
        (member_expression) @name
      ]))) @reference.base

(abstract_class_declaration
  name: (type_identifier) @source
  (class_heritage
    (extends_clause
      value: [
        (identifier) @name
        (type_identifier) @name
        (member_expression) @name
      ]))) @reference.base

(class_declaration
  name: (type_identifier) @source
  (class_heritage
    (implements_clause
      [
        (type_identifier) @name
        (generic_type (type_identifier) @name)
      ]))) @reference.implements

(abstract_class_declaration
  name: (type_identifier) @source
  (class_heritage
    (implements_clause
      [
        (type_identifier) @name
        (generic_type (type_identifier) @name)
      ]))) @reference.implements

(interface_declaration
  name: (type_identifier) @source
  (extends_type_clause
    [
      (type_identifier) @name
      (generic_type (type_identifier) @name)
    ])) @reference.base

; ---- overrides ----

(method_definition
  (override_modifier)
  name: (property_identifier) @name) @reference.override

; ---- calls (full member_expression, not leaf-only) ----

(call_expression
  function: (identifier) @name) @reference.call

(call_expression
  function: (member_expression) @name) @reference.call

; ---- imports (module specifier; type-only imports included) ----

(import_statement
  source: (string) @name) @reference.import
