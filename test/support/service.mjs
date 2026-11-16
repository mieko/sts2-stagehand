import { after } from 'node:test';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { Sts2 } from '../../client/sts2.mjs';

// Cross-runtime fixture: native reads/actions are fakes, but sequencing and
// settlement execute in the production C# service over JSON, not copied JS rules.
let closing = false;
let child,
  current,
  tail = Promise.resolve(),
  timer;
const sessions = new Map();
let next = 0;

function command(params) {
  const result = tail.then(
    () =>
      new Promise((resolve, reject) => {
        if (!child) {
          child = spawn(
            'dotnet',
            [
              fileURLToPath(
                new URL('../bin/WorkflowBridge/WorkflowBridge.test.dll', import.meta.url),
              ),
            ],
            { stdio: ['pipe', 'pipe', 'inherit'] },
          );
          createInterface({ input: child.stdout }).on('line', async (line) => {
            const value = JSON.parse(line);
            if (value.call) {
              try {
                const { endpoint, client } = sessions.get(value.session);
                let result;
                if (value.call === 'read')
                  result = (await (endpoint.nativeObserve ?? endpoint.observe).call(endpoint))
                    .value;
                if (value.call === 'request') result = await endpoint.invoke('request', value.args);
                if (['browse', 'travel', 'popMenu'].includes(value.call)) {
                  if (endpoint[value.call]) result = await endpoint[value.call](value.args);
                  else if (value.call === 'browse')
                    result = await client.object(value.args).call('OpenInventory');
                  else throw new Error(`Missing native fixture port: ${value.call}`);
                }
                if (value.call === 'activate') {
                  const choice = value.args;
                  if (endpoint.activate) result = await endpoint.activate(choice);
                  else if (choice.activation?.kind === 'select_card')
                    result = await endpoint.invoke('select_card', { card: choice.activation.card });
                  else if (choice.activation) {
                    const { target, signal, argument } = choice.activation;
                    result = await client
                      .object(target)
                      .call('emit_signal', signal, client.object(argument));
                  } else
                    result = await client
                      .object(choice.ref)
                      .call(choice.type === 'NCharacterSelectButton' ? 'Select' : 'ForceClick');
                }
                child.stdin.write(JSON.stringify({ result: result ?? {} }) + '\n');
              } catch (error) {
                child.stdin.write(
                  JSON.stringify({
                    error: error.message,
                    activationRejected: error.activationRejected === true,
                  }) + '\n',
                );
              }
            } else {
              const waiting = current;
              current = undefined;
              if (value.error) waiting.reject(new Error(value.error));
              else waiting.resolve(value.result);
            }
          });
          child.on('error', (error) => current?.reject(error));
          child.on('exit', (code) =>
            current?.reject(new Error(`Managed test service exited: ${code}`)),
          );
        }
        current = { resolve, reject };
        child.stdin.write(JSON.stringify(params) + '\n');
      }),
  );
  tail = result.catch(() => {});
  return result;
}

async function tick() {
  try {
    const states = await command({ method: 'tick' });
    for (const [id, summary] of Object.entries(states)) {
      const session = sessions.get(id);
      if (JSON.stringify(summary) !== JSON.stringify(session.summary)) {
        session.summary = summary;
        for (const notify of session.listeners) notify();
      }
    }
  } finally {
    timer = undefined;
    if (
      !closing &&
      [...sessions.values()].some((s) => ['accepted', 'running'].includes(s.summary?.status))
    )
      timer = setTimeout(tick, 1);
  }
}

export class ServiceSts2 extends Sts2 {
  constructor(client, endpoint) {
    const id = String(++next);
    const session = { endpoint, client, listeners: new Set(), summary: null };
    sessions.set(id, session);
    const wrapped = {
      id: endpoint.id,
      async observe() {
        const state = await endpoint.observe();
        const operation = session.summary
          ? await command({ method: 'observe', session: id, state: state.value })
          : null;
        return { ...state, value: { ...state.value, operation } };
      },
      async subscribe(event, listener) {
        session.listeners.add(listener);
        const stop = await endpoint.subscribe(event, listener);
        return async () => {
          session.listeners.delete(listener);
          await stop();
        };
      },
      async invoke(method, args) {
        if (method === 'operate') {
          const accepted = await command({ method: 'start', session: id, args });
          session.summary = { operation: accepted.operation, kind: args.kind, status: 'accepted' };
          if (!timer) timer = setTimeout(tick, 1);
          return accepted;
        }
        if (method === 'operation')
          return command({ method: 'status', session: id, operation: args.operation });
        return endpoint.invoke(method, args);
      },
    };
    super(client, wrapped);
  }
}

after(async () => {
  closing = true;
  clearTimeout(timer);
  await tail;
  child?.stdin.end();
});
