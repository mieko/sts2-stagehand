// Opens Settings and returns to the menu in a running qualification copy.
// STAGEHAND_URL=ws://127.0.0.1:PORT STAGEHAND_TOKEN=... node prove.mjs
import assert from 'node:assert/strict';
import { writeFile } from 'node:fs/promises';
import { connect, GodotValue } from 'godot-stagehand';

const game = await connect(process.env.STAGEHAND_URL, { token: process.env.STAGEHAND_TOKEN });
try {
  const tree = await game.builtin('SceneTree');
  const input = await game.builtin('Input');
  const screen = await game.builtin('Screen');
  const { root } = await tree.invoke('current');
  const menu = (await tree.invoke('query', { name: 'MainMenu' })).nodes[0].node;
  const settings = (await tree.invoke('query', { root: menu, name: 'SettingsScreen' })).nodes[0].node;
  const button = (await tree.invoke('query', { root: menu, name: 'SettingsButton' })).nodes[0].node;
  assert.equal(await settings.get('visible'), false, 'Start at the main menu');

  async function click(control) {
    const [x, y, width, height] = (await control.call('get_global_rect')).args;
    // Input events use window coordinates; controls report logical viewport units.
    const [a, b, c, d, tx, ty] = (await root.call('get_final_transform')).args;
    const cx = x + width / 2, cy = y + height / 2;
    const position = new GodotValue({ type: 'Vector2', args: [a * cx + c * cy + tx, b * cx + d * cy + ty] });
    const result = await input.moveMouseTo({ position, duration_ms: 250 });
    assert.equal(result.status, 'completed');
    assert.ok(result.position.args.every((value, i) => Math.abs(value - position.args[i]) < 0.001));
    await input.invoke('press', { button: 'left', position });
    await input.invoke('release', { button: 'left' });
    return result;
  }

  async function transition(control, expected) {
    let timer, stop;
    const changed = new Promise((resolve, reject) => {
      timer = setTimeout(() => reject(new Error('Visibility event did not arrive')), 3000);
      stop = resolve;
    });
    changed.catch(() => {});
    const unsubscribe = await settings.subscribe('visibility_changed', stop);
    try {
      const movement = await click(control);
      await changed;
      assert.equal(await settings.get('visible'), expected);
      return movement;
    } finally {
      clearTimeout(timer);
      await unsubscribe();
    }
  }

  const opened = await transition(button, true);
  await new Promise(resolve => setTimeout(resolve, 400));
  const image = await screen.capture();
  if (process.env.STAGEHAND_CAPTURE_PATH) await writeFile(process.env.STAGEHAND_CAPTURE_PATH, image.image);
  const back = (await tree.invoke('query', { root: settings, name: 'BackButton' })).nodes[0].node;
  const closed = await transition(back, false);
  await new Promise(resolve => setTimeout(resolve, 400));
  const state = (await input.observe()).value;
  assert.deepEqual(state.held_inputs, []);
  console.log(JSON.stringify({ engine: game.session.engine.string, instance: game.session.instance,
    settingsOpened: true, settingsClosed: true, visibilityEvents: 2,
    capture: { width: image.width, height: image.height, bytes: image.image.length },
    mouseSamples: [opened.samples, closed.samples], inputsReleased: true }, null, 2));
} finally {
  game.close();
}
