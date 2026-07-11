use std::path::Path;
use std::ptr;

mod vmbc;

const STATUS_OK: i32 = 0;
const STATUS_COMPILE_ERROR: i32 = 1;
const STATUS_INVALID_ARGUMENT: i32 = 2;
const STATUS_PANIC: i32 = 3;

#[unsafe(no_mangle)]
pub extern "C" fn pdvm_compile_file_utf8(
    path_ptr: *const u8,
    path_len: usize,
    output_ptr: *mut *mut u8,
    output_len: *mut usize,
) -> i32 {
    if output_ptr.is_null() || output_len.is_null() {
        return STATUS_INVALID_ARGUMENT;
    }
    unsafe {
        *output_ptr = ptr::null_mut();
        *output_len = 0;
    }
    if path_ptr.is_null() || path_len == 0 {
        write_output(output_ptr, output_len, b"source path is empty".to_vec());
        return STATUS_INVALID_ARGUMENT;
    }

    let result = std::panic::catch_unwind(|| {
        let path_bytes = unsafe { std::slice::from_raw_parts(path_ptr, path_len) };
        let path_text = std::str::from_utf8(path_bytes)
            .map_err(|error| (STATUS_INVALID_ARGUMENT, error.to_string()))?;
        let compiled = vm::compile_source_file(Path::new(path_text))
            .map_err(|error| (STATUS_COMPILE_ERROR, error.to_string()))?;
        vmbc::encode_program(&compiled.program)
            .map_err(|error| (STATUS_COMPILE_ERROR, error))
    });

    match result {
        Ok(Ok(vmbc)) => {
            write_output(output_ptr, output_len, vmbc);
            STATUS_OK
        }
        Ok(Err((status, message))) => {
            write_output(output_ptr, output_len, message.into_bytes());
            status
        }
        Err(payload) => {
            let message = payload
                .downcast_ref::<String>()
                .cloned()
                .or_else(|| payload.downcast_ref::<&str>().map(|value| (*value).to_owned()))
                .unwrap_or_else(|| "pd-vm compiler panicked".to_owned());
            write_output(output_ptr, output_len, message.into_bytes());
            STATUS_PANIC
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn pdvm_free_buffer(buffer_ptr: *mut u8, buffer_len: usize) {
    if buffer_ptr.is_null() {
        return;
    }
    unsafe {
        let slice = ptr::slice_from_raw_parts_mut(buffer_ptr, buffer_len);
        drop(Box::from_raw(slice));
    }
}

fn write_output(output_ptr: *mut *mut u8, output_len: *mut usize, bytes: Vec<u8>) {
    let mut bytes = bytes.into_boxed_slice();
    let pointer = bytes.as_mut_ptr();
    let length = bytes.len();
    std::mem::forget(bytes);
    unsafe {
        *output_ptr = pointer;
        *output_len = length;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_empty_path() {
        let mut output = ptr::null_mut();
        let mut length = 0;
        let status = pdvm_compile_file_utf8(ptr::null(), 0, &mut output, &mut length);
        assert_eq!(status, STATUS_INVALID_ARGUMENT);
        assert!(!output.is_null());
        pdvm_free_buffer(output, length);
    }
}
