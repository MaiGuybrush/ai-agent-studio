import { parseSse, SseMessage } from './sse';

function body(...chunks: string[]): ReadableStream<Uint8Array> {
  const enc = new TextEncoder();
  return new ReadableStream({
    start(c) {
      chunks.forEach((x) => c.enqueue(enc.encode(x)));
      c.close();
    },
  });
}
async function all(s: ReadableStream<Uint8Array>): Promise<SseMessage[]> {
  const out: SseMessage[] = [];
  for await (const m of parseSse(s)) out.push(m);
  return out;
}

describe('parseSse', () => {
  it('parses id/event/data blocks even when chunks split mid-line', async () => {
    const msgs = await all(
      body('id: 1\nevent: text\ndata: {"text":"哈', '囉"}\n\nid: 2\nevent: done\n', 'data: {"usage":{}}\n\n'),
    );
    expect(msgs).toEqual([
      { id: '1', type: 'text', data: { text: '哈囉' } },
      { id: '2', type: 'done', data: { usage: {} } },
    ]);
  });

  it('handles CRLF and ignores a trailing incomplete block', async () => {
    const msgs = await all(body('id: 1\r\nevent: text\r\ndata: {"text":"a"}\r\n\r\nid: 2\r\nevent: te'));
    expect(msgs.map((m) => m.id)).toEqual(['1']);
  });
});
