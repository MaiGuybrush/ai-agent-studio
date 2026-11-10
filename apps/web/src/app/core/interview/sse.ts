export interface SseMessage {
  id: string | null;
  type: string;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  data: any;
}

/** 把 fetch 的 response body 解成 SSE 事件（EventSource 不能帶 Authorization 標頭，所以自己解析）。 */
export async function* parseSse(body: ReadableStream<Uint8Array>): AsyncGenerator<SseMessage> {
  const reader = body.getReader();
  const dec = new TextDecoder();
  let buf = '';
  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) return;
      buf += dec.decode(value, { stream: true });
      let i: number;
      while ((i = buf.search(/\r?\n\r?\n/)) >= 0) {
        const block = buf.slice(0, i);
        buf = buf.slice(i).replace(/^\r?\n\r?\n/, '');
        const m = toMessage(block);
        if (m) yield m;
      }
    }
  } finally {
    reader.cancel().catch(() => undefined);
  }
}

function toMessage(block: string): SseMessage | null {
  let id: string | null = null;
  let type = 'message';
  let data = '';
  for (const line of block.split(/\r?\n/)) {
    if (line.startsWith('id:')) id = line.slice(3).trim();
    else if (line.startsWith('event:')) type = line.slice(6).trim();
    else if (line.startsWith('data:')) data += line.slice(5).trim();
  }
  return data ? { id, type, data: JSON.parse(data) } : null;
}
