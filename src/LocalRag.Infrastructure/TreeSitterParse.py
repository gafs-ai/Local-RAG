# Copyright (C) 2026 GAFS Inc. SPDX-License-Identifier: GPL-3.0-or-later
# Input is source data; never compile, evaluate, import, or execute the target code.
import importlib
import json
import sys

request = json.load(sys.stdin)
sys.path[:0] = request["paths"]
from tree_sitter import Language, Parser

grammar = request["grammar"]
binding = importlib.import_module("tree_sitter_" + grammar)
if grammar == "typescript":
    capsule = binding.language_tsx() if request["tsx"] else binding.language_typescript()
else:
    capsule = binding.language()
source = request["code"].encode("utf-8")
root = Parser(Language(capsule)).parse(source).root_node
if root.has_error:
    pending = [root]
    while pending:
        node = pending.pop()
        # Kotlin 1.1.0 can emit a missing member terminator immediately before the
        # closing class brace. This zero-width separator consumes no source text.
        terminal_separator = (grammar == "kotlin" and node.is_missing and node.type == "_class_member_semi"
                              and source[node.start_byte:].lstrip().startswith(b"}"))
        if node.is_error or node.is_missing and not terminal_separator:
            sys.exit(2)
        pending.extend(node.children)

types = {
    "class_declaration", "class", "interface_declaration", "enum_declaration",
    "record_declaration", "annotation_type_declaration", "class_specifier",
    "struct_specifier", "union_specifier", "enum_specifier", "struct_item",
    "enum_item", "trait_item", "union_item", "impl_item", "object_declaration",
    "companion_object",
}
functions = {
    "function_declaration", "generator_function_declaration", "method_definition",
    "method_declaration", "constructor_declaration", "compact_constructor_declaration",
    "function_definition", "function_item", "function_signature_item",
    "secondary_constructor", "getter", "setter", "method_signature", "abstract_method_signature",
}
expressions = {"arrow_function", "function_expression", "generator_function"}
units = []

def text(node):
    return source[node.start_byte:node.end_byte].decode("utf-8") if node else ""

def documentation(node):
    comments = []
    previous = node.prev_named_sibling
    line = node.start_point.row
    while previous and "comment" in previous.type and line - previous.end_point.row <= 1:
        comments.insert(0, text(previous))
        line = previous.start_point.row
        previous = previous.prev_named_sibling
    return "\n".join(comments)

def variables(node):
    names = []
    fields = {"field_declaration", "field_definition", "public_field_definition", "property_declaration", "const_item"}
    def declared(part):
        name = part.child_by_field_name("name") or part.child_by_field_name("property")
        if name:
            names.append(text(name))
            return
        declarators = part.children_by_field_name("declarator")
        if declarators:
            for declarator in declarators:
                if declarator.type == "variable_declarator":
                    declared(declarator)
                    continue
                while declarator.child_by_field_name("declarator"):
                    declarator = declarator.child_by_field_name("declarator")
                names.append(text(declarator))
            return
        for child in part.named_children:
            if child.type in {"variable_declarator", "variable_declaration"}:
                declared(child)
            elif grammar == "kotlin" and child.type in {"simple_identifier", "identifier"}:
                names.append(text(child))
    def walk(part):
        if part.type in types or part.type in functions or part.type in expressions:
            return
        if part.type in fields:
            declared(part)
            return
        if grammar == "kotlin" and part.type == "class_parameter" and any(c.type in {"val", "var"} for c in part.children):
            declared(part)
            return
        for child in part.named_children:
            walk(child)
    for child in node.named_children:
        walk(child)
    return list(dict.fromkeys(names))

def name_of(node):
    name = node.child_by_field_name("name")
    if name:
        return text(name)
    if node.type == "impl_item":
        return "impl " + text(node.child_by_field_name("type"))
    if node.type == "function_definition":
        decl = node.child_by_field_name("declarator")
        while decl:
            nested = decl.child_by_field_name("declarator")
            if not nested:
                return text(decl)
            decl = nested
    # Kotlin's grammar uses unnamed identifier fields for declarations.
    if grammar == "kotlin":
        for child in node.named_children:
            if child.type in {"simple_identifier", "type_identifier", "identifier"}:
                return text(child)
        if node.type in {"secondary_constructor", "getter", "setter", "companion_object"}:
            return node.type
    if node.type in expressions and node.parent:
        parent = node.parent
        if parent.type in {"variable_declarator", "pair", "assignment_expression"}:
            return text(parent.child_by_field_name("name") or parent.child_by_field_name("key") or parent.child_by_field_name("left"))
    return ""

def visit(node, container):
    kind = "class" if node.type in types else "function" if node.type in functions or node.type in expressions else None
    name = name_of(node) if kind else ""
    if kind and name:
        if len(units) >= 999:
            raise ValueError("unit limit")
        span = node
        if node.parent and node.parent.type in {"export_statement", "template_declaration", "decorated_definition"}:
            span = node.parent
        if node.type in expressions and node.parent and node.parent.type == "variable_declarator":
            span = node.parent
        ident = "u" + str(len(units) + 1)
        units.append({"Id": ident, "ContainerId": container, "Kind": kind, "Name": name[:200],
                      "StartByte": span.start_byte, "EndByte": span.end_byte,
                      "StartLine": span.start_point.row + 1, "EndLine": span.end_point.row + 1,
                      "Documentation": documentation(span), "Variables": variables(node) if kind == "class" else []})
        container = ident
    for child in node.named_children:
        visit(child, container)

try:
    visit(root, "file")
    first = next((n for n in root.named_children if "comment" not in n.type), None)
    file_doc = documentation(first) if first and first.type not in types | functions | expressions else ""
    print(json.dumps({"Documentation": file_doc, "Units": units}, ensure_ascii=True))
except (ValueError, RecursionError):
    sys.exit(2)
