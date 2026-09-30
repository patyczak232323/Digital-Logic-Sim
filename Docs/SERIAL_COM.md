# Windows virtual COM and PuTTY

## Intended connection

Use a paired virtual serial driver such as com0com:

    Rewired -> COM10 <====> COM11 <- PuTTY

Rewired and PuTTY must open different ends. A serial port is opened exclusively.

## PuTTY settings

- Connection type: Serial
- Serial line: COM11
- Speed: 115200
- Data bits: 8
- Stop bits: 1
- Parity: None
- Flow control: None

Configure the Rewired component to open COM10 at the same speed.

## Circuit protocol

### Transmit

1. Wait until TX READY is high.
2. Put a byte on TX DATA.
3. Create a rising edge on TX SEND.
4. Lower TX SEND before sending the next byte.

Keeping TX SEND high sends exactly one byte.

### Receive

1. Wait until RX VALID is high.
2. Read RX DATA.
3. Create a rising edge on RX READ.
4. Lower RX READ before acknowledging another byte.

RX DATA remains stable while RX VALID is high. The next buffered byte may become visible on the next simulation step.

### Reset

While RESET is high, the component clears its managed RX/TX queues and asks Windows to clear the driver buffers. No byte is transmitted during reset.

## Implementation details

WindowsComEndpoint opens the selected device through CreateFileW and configures 8N1 communication through the Win32 DCB API. A background worker performs blocking operating-system I/O with short timeouts.

ByteSerialAdapter never performs operating-system I/O. It only exchanges bytes with the endpoint queues at simulation-step boundaries. This prevents a slow terminal from blocking the simulation thread.

Each queue is limited to 256 bytes. TX READY becomes low when the outgoing queue is full. The circuit must obey TX READY; an attempted transmit while unavailable is rejected and counted for diagnostics.

The first version connects to an existing COM device or virtual pair. Creating a virtual Windows port inside the application is intentionally excluded because that requires installing and maintaining a kernel driver with administrator privileges.
