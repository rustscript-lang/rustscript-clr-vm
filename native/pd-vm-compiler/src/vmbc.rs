use vm::compiler::TypeSchema;
use vm::{DebugInfo, Program, TypeMap, Value};

const VERSION: u16 = 8;

pub fn encode_program(program: &Program) -> Result<Vec<u8>, String> {
    let mut out = Vec::new();
    out.extend_from_slice(b"VMBC");
    out.extend_from_slice(&VERSION.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    write_count("constants", program.constants.len(), &mut out)?;
    for constant in &program.constants {
        match constant {
            Value::Null => out.push(4),
            Value::Int(value) => {
                out.push(0);
                out.extend_from_slice(&value.to_le_bytes());
            }
            Value::Float(value) => {
                out.push(3);
                out.extend_from_slice(&value.to_le_bytes());
            }
            Value::Bool(value) => {
                out.push(1);
                out.push(u8::from(*value));
            }
            Value::String(value) => {
                out.push(2);
                let value = repair_utf8_mojibake(value);
                write_bytes("constant string", value.as_bytes(), &mut out)?;
            }
            Value::Bytes(value) => {
                out.push(5);
                write_bytes("constant bytes", value.as_slice(), &mut out)?;
            }
            Value::Array(_) => return Err("array constants cannot be encoded as VMBC".to_owned()),
            Value::Map(_) => return Err("map constants cannot be encoded as VMBC".to_owned()),
        }
    }

    write_bytes("code", &program.code, &mut out)?;
    write_count("imports", program.imports.len(), &mut out)?;
    for import in &program.imports {
        write_string("import name", &import.name, &mut out)?;
        out.push(import.arity);
        out.push(import.return_type as u8);
    }
    write_type_map(&mut out, program.type_map.as_ref())?;
    write_debug_info(&mut out, program.debug.as_ref())?;
    Ok(out)
}

fn write_type_map(out: &mut Vec<u8>, type_map: Option<&TypeMap>) -> Result<(), String> {
    let Some(type_map) = type_map else {
        out.push(0);
        return Ok(());
    };
    out.push(1);
    out.push(u8::from(type_map.strict_types));
    write_count("type map locals", type_map.local_types.len(), out)?;
    out.extend(type_map.local_types.iter().map(|value| *value as u8));
    for schema in &type_map.local_schemas {
        write_optional_schema(schema.as_ref(), out)?;
    }
    write_bools("type map callable slots", &type_map.callable_slots, out)?;
    write_bools("type map optional slots", &type_map.optional_slots, out)?;
    write_count("type map operands", type_map.operand_types.len(), out)?;
    let mut operands = type_map.operand_types.iter().collect::<Vec<_>>();
    operands.sort_unstable_by_key(|(offset, _)| **offset);
    for (offset, (lhs, rhs)) in operands {
        write_count("type map operand offset", *offset, out)?;
        out.push(*lhs as u8);
        out.push(*rhs as u8);
    }
    Ok(())
}

fn write_debug_info(out: &mut Vec<u8>, debug: Option<&DebugInfo>) -> Result<(), String> {
    let Some(debug) = debug else {
        out.push(0);
        return Ok(());
    };
    out.push(1);
    match &debug.source {
        Some(source) => {
            out.push(1);
            write_string("debug source", source, out)?;
        }
        None => out.push(0),
    }
    write_count("debug lines", debug.lines.len(), out)?;
    for line in &debug.lines {
        out.extend_from_slice(&line.offset.to_le_bytes());
        out.extend_from_slice(&line.line.to_le_bytes());
    }
    write_count("debug functions", debug.functions.len(), out)?;
    for function in &debug.functions {
        write_string("debug function name", &function.name, out)?;
        write_count("debug function args", function.args.len(), out)?;
        for argument in &function.args {
            write_string("debug arg name", &argument.name, out)?;
            out.push(argument.position);
        }
    }
    write_count("debug locals", debug.locals.len(), out)?;
    for local in &debug.locals {
        write_string("debug local name", &local.name, out)?;
        out.push(local.index);
        write_optional_u32(local.declared_line, out);
        write_optional_u32(local.last_line, out);
    }
    Ok(())
}

fn write_optional_schema(schema: Option<&TypeSchema>, out: &mut Vec<u8>) -> Result<(), String> {
    match schema {
        Some(schema) => {
            out.push(1);
            write_schema(schema, out)
        }
        None => {
            out.push(0);
            Ok(())
        }
    }
}

fn write_schema(schema: &TypeSchema, out: &mut Vec<u8>) -> Result<(), String> {
    match schema {
        TypeSchema::Unknown => out.push(0),
        TypeSchema::Null => out.push(1),
        TypeSchema::Int => out.push(2),
        TypeSchema::Float => out.push(3),
        TypeSchema::Number => out.push(4),
        TypeSchema::Bool => out.push(5),
        TypeSchema::String => out.push(6),
        TypeSchema::Bytes => out.push(7),
        TypeSchema::Optional(inner) => {
            out.push(16);
            write_schema(inner, out)?;
        }
        TypeSchema::GenericParam(name) => {
            out.push(8);
            write_string("schema generic", name, out)?;
        }
        TypeSchema::Named(name, arguments) => {
            out.push(9);
            write_string("schema name", name, out)?;
            write_count("schema type args", arguments.len(), out)?;
            for argument in arguments {
                write_schema(argument, out)?;
            }
        }
        TypeSchema::Array(item) => {
            out.push(10);
            write_schema(item, out)?;
        }
        TypeSchema::ArrayTuple(items) => {
            out.push(11);
            write_count("schema tuple items", items.len(), out)?;
            for item in items {
                write_schema(item, out)?;
            }
        }
        TypeSchema::ArrayTupleRest { prefix, rest } => {
            out.push(12);
            write_count("schema tuple prefix", prefix.len(), out)?;
            for item in prefix {
                write_schema(item, out)?;
            }
            write_schema(rest, out)?;
        }
        TypeSchema::Map(item) => {
            out.push(13);
            write_schema(item, out)?;
        }
        TypeSchema::Object(fields) => {
            out.push(14);
            let mut fields = fields.iter().collect::<Vec<_>>();
            fields.sort_unstable_by(|(left, _), (right, _)| left.cmp(right));
            write_count("schema object fields", fields.len(), out)?;
            for (name, value) in fields {
                write_string("schema object field", name, out)?;
                write_schema(value, out)?;
            }
        }
        TypeSchema::Callable { params, result } => {
            out.push(15);
            write_count("schema callable params", params.len(), out)?;
            for parameter in params {
                write_schema(parameter, out)?;
            }
            write_schema(result, out)?;
        }
    }
    Ok(())
}

fn write_optional_u32(value: Option<u32>, out: &mut Vec<u8>) {
    match value {
        Some(value) => {
            out.push(1);
            out.extend_from_slice(&value.to_le_bytes());
        }
        None => out.push(0),
    }
}

fn write_bools(field: &'static str, values: &[bool], out: &mut Vec<u8>) -> Result<(), String> {
    write_count(field, values.len(), out)?;
    out.extend(values.iter().map(|value| u8::from(*value)));
    Ok(())
}

fn write_string(field: &'static str, value: &str, out: &mut Vec<u8>) -> Result<(), String> {
    let value = repair_utf8_mojibake(value);
    write_bytes(field, value.as_bytes(), out)
}

fn repair_utf8_mojibake(value: &str) -> std::borrow::Cow<'_, str> {
    if value.is_ascii() {
        return std::borrow::Cow::Borrowed(value);
    }

    let mut bytes = Vec::with_capacity(value.len());
    for character in value.chars() {
        let Ok(byte) = u8::try_from(u32::from(character)) else {
            return std::borrow::Cow::Borrowed(value);
        };
        bytes.push(byte);
    }

    match String::from_utf8(bytes) {
        Ok(decoded) if decoded != value => std::borrow::Cow::Owned(decoded),
        _ => std::borrow::Cow::Borrowed(value),
    }
}

fn write_bytes(field: &'static str, bytes: &[u8], out: &mut Vec<u8>) -> Result<(), String> {
    write_count(field, bytes.len(), out)?;
    out.extend_from_slice(bytes);
    Ok(())
}

fn write_count(field: &'static str, count: usize, out: &mut Vec<u8>) -> Result<(), String> {
    let count =
        u32::try_from(count).map_err(|_| format!("{field} length is too large: {count}"))?;
    out.extend_from_slice(&count.to_le_bytes());
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::repair_utf8_mojibake;

    #[test]
    fn repairs_utf8_bytes_expanded_as_latin1_characters() {
        let mojibake = String::from_iter(['\u{f0}', '\u{9f}', '\u{98}', '\u{b5}']);
        assert_eq!(repair_utf8_mojibake(&mojibake), "😵");
    }

    #[test]
    fn preserves_text_that_is_already_unicode() {
        assert_eq!(repair_utf8_mojibake("🙂"), "🙂");
        assert_eq!(repair_utf8_mojibake("é"), "é");
        assert_eq!(repair_utf8_mojibake("plain ASCII"), "plain ASCII");
    }
}
