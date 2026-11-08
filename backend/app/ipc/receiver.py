"""Request dispatch with per-command failures and a persistent image registry."""

import hashlib
import logging
from app.ipc.named_pipe_server import NamedPipeServer, PipeConnection
from app.ipc.protocol import ProtocolError, parse_command
from app.ipc.error_responses import error_response
from app.ipc.shared_memory_io import read_shared_image, write_shared_image
from app.models.messages import CommandMessage, ImportImageMessage, GrayscaleMessage, OperationMessage, PluginResultsMessage
from app.models.image_types import ImageRecord
from app.storage.image_registry import ImageRegistry
from app.operation_service import execute_operation, describe_analysis
from app.models.transforms import output_size
from app.runtime_info import SOURCE_REVISION, PROCESS_ID

logger = logging.getLogger(__name__)


def receive_command(connection: PipeConnection) -> CommandMessage:
    """Read and validate one newline-delimited JSON command.

    Args:
        connection (PipeConnection): Connected, buffered Windows byte-stream pipe.
    Returns:
        CommandMessage: Validated version 1 command; imports describe (h,w,4) BGRA8 pixels.
    Raises:
        ProtocolError: Invalid envelope; EOFError/OSError: transport failure.
    """
    return parse_command(connection.read_line())


def receive_images(message: ImportImageMessage, registry: ImageRegistry) -> ImageRecord:
    """Read shared memory and register an independent original image.

    Args:
        message (ImportImageMessage): Validated identity and uint8 BGRA8 mapping descriptor.
        registry (ImageRegistry): Persistent session-owned image store.
    Returns:
        ImageRecord: Registered metadata and immutable pixels of shape (height, width, 4).
    Raises:
        ValueError: Invalid descriptor or duplicate identity.
        OSError: Mapping cannot be opened/read. MemoryError: Registry capacity exceeded.
    """
    p = message.pixel_buffer
    image = read_shared_image(p.mapping_name, p.width, p.height, p.channels, p.row_stride_bytes, p.buffer_size_bytes)
    return registry.register_image(message.image_id or "", message.file_name, image)


def receive_grayscale(message: GrayscaleMessage, registry: ImageRegistry) -> ImageRecord:
    """Convert a stored image, register a new result and write its UI-owned output mapping.

    Args:
        message (GrayscaleMessage): Source/result identities, optional pixel rectangle,
            and existing writable mapping matching the source (height, width, 4).
        registry (ImageRegistry): Persistent owner of immutable uint8 BGRA8 arrays.
    Returns:
        ImageRecord: New grayscale BGRA8 image with source alpha and dimensions.
    Raises:
        ValueError: Missing source, duplicate identity, invalid ROI or output dimensions.
        OSError: Mapping access/write failure; the newly registered result is rolled back.
        MemoryError: Insufficient registry capacity. cv2.error: Color conversion failure.
    """
    source = registry.get_image(message.image_id or "")
    if source is None:
        raise ValueError("Source image is not registered")
    output = message.output_buffer
    if (output.width, output.height) != (source.metadata.width, source.metadata.height):
        raise ValueError("Output dimensions must match the source image")
    converted = execute_operation("to_grayscale", {}, message.image_id or "", message.selection, registry)
    record = registry.register_image(message.result_image_id, message.file_name, converted)
    try:
        write_shared_image(output.mapping_name, record.pixels)
    except (OSError, ValueError, MemoryError):
        registry.remove_image(message.result_image_id)
        raise
    return record


def receive_operation(message: OperationMessage, registry: ImageRegistry) -> dict[str, object]:
    """Execute a prepared operation and publish a derived image or typed analysis response.

    Args:
        message (OperationMessage): Validated parameters and optional UI-owned output map.
        registry (ImageRegistry): Owner of uint8 BGRA8 (height, width, 4) originals/results.
    Returns:
        dict[str, object]: Image metadata, an RGBA color or a bounded color-frequency palette.
    Raises:
        ValueError: Invalid source/output shape. OSError: Mapping write failed (rolled back).
        NotImplementedError: Selected function is still a placeholder. User implementation
        exceptions propagate to the per-request error boundary without stopping the server.
    """
    if message.operation == "run_plugin":
        masks = [read_shared_image(p.mapping_name, p.width, p.height, p.channels,
                                   p.row_stride_bytes, p.buffer_size_bytes) for p in message.mask_buffers]
        # The existing BGRA8 mapping transports replicated mask bytes, never a color layer.
        from app.plugins.masks import decode_mask
        masks = [decode_mask(mask) for mask in masks]
        outputs = execute_operation(message.operation, message.params, message.image_id or "",
                                    message.selection, registry, masks=masks)
        if message.output_buffer is None:
            records = registry.stage_plugin_results(message.result_image_id, message.file_name, outputs)
            return {"message_type": "plugin_results_ready", "result_image_id": message.result_image_id,
                    "images": [describe(record) for record in records]}
        # Compatibility for existing clients that preallocate a single output.
        output = message.output_buffer
        if len(outputs) != 1 or outputs[0].shape[:2] != (output.height, output.width):
            raise ValueError("Use plugin result negotiation for multiple or resized outputs")
        record = registry.register_image(message.result_image_id, message.file_name, outputs[0])
        try:
            write_shared_image(output.mapping_name, record.pixels)
        except Exception:
            registry.remove_image(record.metadata.image_id)
            raise
        metadata = describe(record)
        result_id = metadata.pop("image_id")
        return {"message_type": "image_processed", "result_image_id": result_id, **metadata}
    if message.output_buffer is not None:
        source = registry.get_image(message.image_id or "")
        if source is None:
            raise ValueError("Source image is not registered")
        expected = output_size(message.operation, message.params, source.metadata.width, source.metadata.height)
        if (message.output_buffer.width, message.output_buffer.height) != expected:
            raise ValueError("Output dimensions must match the requested operation")
    mask = None
    if message.mask_buffer is not None:
        p = message.mask_buffer
        if (p.width, p.height) != (source.metadata.width, source.metadata.height):
            raise ValueError("Mask dimensions must match the source image")
        if p.mapping_name == message.output_buffer.mapping_name:
            raise ValueError("Mask and output must use different mappings")
        mask = read_shared_image(p.mapping_name, p.width, p.height, p.channels, p.row_stride_bytes, p.buffer_size_bytes)
    converted = execute_operation(message.operation, message.params, message.image_id or "", message.selection, registry, mask=mask)
    if message.output_buffer is None:
        return describe_analysis(converted)
    record = registry.register_image(message.result_image_id or "", message.file_name or "", converted)
    try:
        write_shared_image(message.output_buffer.mapping_name, record.pixels)
    except Exception:
        registry.remove_image(record.metadata.image_id)
        raise
    metadata = describe(record)
    result_id = metadata.pop("image_id")
    return {"message_type": "image_processed", "result_image_id": result_id, **metadata}


def describe(record: ImageRecord) -> dict[str, object]:
    """Return JSON metadata and SHA-256 of record's (h,w,4) BGRA8 pixels, without pixels."""
    return {"image_id": record.metadata.image_id, "file_name": record.metadata.file_name,
            "width": record.metadata.width, "height": record.metadata.height,
            "sha256": hashlib.sha256(memoryview(record.pixels)).hexdigest()}


def dispatch(message: CommandMessage, registry: ImageRegistry) -> dict[str, object]:
    """Execute message (CommandMessage) against registry; return correlated response dict.

    Imports store (h,w,4) BGRA8 arrays. Raises ValueError/OSError/MemoryError on failed commands.
    Prepared functions are routed automatically; None results become explicit not_implemented errors.
    """
    result: dict[str, object] = {"protocol_version": 1, "request_id": message.request_id,
                               "image_id": message.image_id, "status": "ok"}
    if isinstance(message, ImportImageMessage):
        record = receive_images(message, registry)
        result.update(message_type="image_imported", batch_id=message.batch_id, **describe(record))
        logger.info("Imported %s %dx%d", record.metadata.image_id, record.metadata.width, record.metadata.height)
    elif isinstance(message, GrayscaleMessage):
        record = receive_grayscale(message, registry)
        metadata = describe(record)
        result_id = metadata.pop("image_id")
        result.update(message_type="image_processed", result_image_id=result_id, **metadata)
        logger.info("Grayscale %s -> %s", message.image_id, result_id)
    elif isinstance(message, PluginResultsMessage):
        result["result_image_id"] = message.result_image_id
        if message.message_type == "release_plugin_results":
            registry.release_plugin_results(message.result_image_id)
            result.update(message_type="plugin_results_released")
        else:
            try:
                ids = registry.plugin_batches.get(message.result_image_id)
                if ids is None or len(ids) != len(message.output_buffers):
                    raise ValueError("Unknown plugin batch or wrong output buffer count")
                records = [registry.get_image(identity) for identity in ids]
                names = [p.mapping_name for p in message.output_buffers]
                if len(set(names)) != len(names):
                    raise ValueError("Each plugin result needs a distinct mapping")
                for record, p in zip(records, message.output_buffers):
                    if record is None or (p.width, p.height) != (record.metadata.width, record.metadata.height):
                        raise ValueError("Plugin output dimensions do not match")
                for record, p in zip(records, message.output_buffers):
                    write_shared_image(p.mapping_name, record.pixels)
                result.update(message_type="plugin_results_copied")
            except Exception:
                registry.release_plugin_results(message.result_image_id)
                raise
    elif isinstance(message, OperationMessage):
        result.update(receive_operation(message, registry))
    elif message.message_type == "remove_image":
        result.update(message_type="image_removed", removed=registry.remove_image(message.image_id or ""))
    elif message.message_type == "list_images":
        result.update(message_type="images_listed", images=[describe(r) for r in registry.records()])
    elif message.message_type == "list_plugins":
        from app.plugins.catalog import get_plugin_registry
        result.update(message_type="plugins_listed", **get_plugin_registry().describe())
    elif message.message_type == "ping":
        result.update(message_type="pong", image_count=len(registry.records()),
                      backend_revision=SOURCE_REVISION, backend_pid=PROCESS_ID)
    elif message.message_type == "shutdown":
        result.update(message_type="shutdown_ack")
    return result


def serve(server: NamedPipeServer, registry: ImageRegistry) -> None:
    """Serve local UI sessions until shutdown, preserving registry after client disconnects.

    Args:
        server (NamedPipeServer): Started listener or configuration for one.
        registry (ImageRegistry): Owner of (h,w,4) BGRA8 image arrays across commands.
    Returns:
        None after shutdown acknowledgement.
    Raises:
        OSError: Listener cannot be created. Invalid individual commands are reported, not raised.
    """
    stop = False
    try:
        while not stop:
            connection = server.accept_connection()
            try:
                while True:
                    message = None
                    try:
                        line = connection.read_line()
                    except ProtocolError as error:
                        connection.send({"protocol_version": 1, "message_type": "error", "request_id": None,
                                         "image_id": None, "status": "error", "code": "frame_too_large", "message": str(error)})
                        break
                    try:
                        message = parse_command(line)
                        response = dispatch(message, registry)
                    except Exception as error:
                        response = error_response(error, message)
                    try:
                        connection.send(response)
                    except ValueError:
                        connection.send({"protocol_version": 1, "message_type": "error", "status": "error",
                                         "request_id": message.request_id if message else None,
                                         "image_id": message.image_id if message else None,
                                         "code": "response_too_large", "message": "Response exceeds the 64 KiB control-message limit"})
                    if message and message.message_type == "shutdown":
                        stop = True
                        break
            except (EOFError, OSError) as error:
                logger.info("Connection ended: %s", error)
            finally:
                registry.clear_plugin_results()
                connection.close()
    finally:
        server.close()


def wait_for_messages() -> None:
    """Initialize a persistent registry and receive UI commands until shutdown.

    Returns None; raises OSError if the per-user named pipe cannot start.
    Images are retained as independent uint8 BGRA8 arrays of shape (height, width, 4).
    """
    server = NamedPipeServer()
    server.start()
    logger.info("READY %s", server.path)
    serve(server, ImageRegistry())

