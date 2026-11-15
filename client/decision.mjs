import { incoming } from './present.mjs';

export function isShopEntrance(state) {
  return ['NMerchantRoom', 'NEventRoom'].includes(state.screen) && Boolean(state.shop);
}

export function decisionKind(state) {
  const screen = state.screen;
  if (screen === 'NTimelineScreen') return 'timeline';

  if (screen === 'NTimelineTutorial' || screen.includes('Ftue')) return 'tutorial';

  if (screen === 'NEpochInspectScreen') return 'epoch';

  if (screen.startsWith('NUnlock')) return 'unlock';

  if (screen === 'CardPreview') return 'card_preview';

  if (screen.includes('Bundle')) return 'bundle_selection';

  if (screen.includes('RelicSelection')) return 'relic_selection';

  if (screen.includes('CrystalSphere')) return 'crystal_sphere';

  if (screen === 'NMapScreen') return 'map';

  if (screen === 'NRewardsScreen') return 'rewards';

  if (screen === 'NCardRewardSelectionScreen') return 'card_reward';

  if (
    screen === 'HandSelection' ||
    /Card.*Select|Select.*Card/.test(screen) ||
    (screen.includes('Select') && state.choices.some((c) => c.type.includes('CardHolder')))
  )
    return 'card_selection';

  if (isShopEntrance(state)) return 'shop_entrance';

  if (screen.includes('Merchant')) return screen.includes('Inventory') ? 'shop' : 'shop_entrance';

  if (screen.includes('RestSite')) return 'rest';

  if (screen.includes('Treasure')) return 'treasure';

  if (screen.includes('Event')) return 'event';

  if (screen.includes('GameOver')) return 'game_over';

  if (state.combat) return 'combat';

  return state.player ? 'choice' : 'menu';
}

export function choiceView({ rect, ...choice }) {
  let kind =
    {
      NMerchantCard: 'buy_card',
      NMerchantRelic: 'buy_relic',
      NMerchantPotion: 'buy_potion',
      NMerchantCardRemoval: 'remove_card',
      NEventOptionButton: 'event_option',
      NAncientDialogueHitbox: 'advance_dialogue',
      NRestSiteButton: 'rest_option',
      NRewardButton: 'reward',
      NGridCardHolder: 'select_card',
      NHandCardHolder: 'select_card',
      NSelectedHandCardHolder: 'deselect_card',
      NCardBundle: 'select_bundle',
      NUnlockConfirmButton: 'confirm_unlock',
      NRelicBasicHolder: 'select_relic',
      NTreasureRoomRelicHolder: 'take_relic',
      NCrystalSphereCell: 'divine_cell',
    }[choice.type] ?? 'ui';
  if (choice.type === 'NRewardButton')
    kind =
      {
        RelicReward: 'relic_reward',
        PotionReward: 'potion_reward',
        GoldReward: 'gold_reward',
        CardReward: 'card_reward',
        SpecialCardReward: 'special_card_reward',
      }[choice.detail?.category] ?? 'reward';
  if (choice.type === 'NEpochSlot')
    kind =
      { Obtained: 'reveal_epoch', Complete: 'inspect_epoch', NotObtained: 'locked_epoch' }[
        choice.detail?.state
      ] ?? 'epoch';
  const model =
    choice.detail?.item ?? choice.detail?.relic ?? choice.detail?.potion ?? choice.detail;
  return {
    ...choice,
    kind,
    price: choice.detail?.price,
    description: model?.description,
    effects: model?.effects,
    cost: model?.cost,
    starCost: model?.starCost,
    target: model?.target,
    upgraded: model?.upgraded,
    enchantment: model?.enchantment,
    affliction: model?.affliction,
    replay: model?.replay,
    values: model?.values,
    valueContext: model?.valueContext,
  };
}

export function decisionView(state) {
  const kind = decisionKind(state);
  const choices = state.choices.map(choiceView);
  const offers = choices.filter((c) =>
    ['take_relic', 'select_relic', 'relic_reward'].includes(c.kind),
  );
  return {
    kind,
    screen: state.screen,
    room: state.room,
    act: state.act,
    floor: state.floor,
    totalFloor: state.totalFloor,
    ascension: state.ascension,
    preview: state.preview,
    ready: state.ready,
    player: state.player,
    combat: state.combat ? { ...state.combat, incoming: incoming(state.combat) } : state.combat,
    pending: state.pending,
    operation: state.operation,
    event: state.event,
    dialogue: state.dialogue,
    selection: state.selection,
    shop: state.shop,
    result: state.result,
    navigation: state.navigation,
    epoch: state.epoch,
    unlock: state.unlock,
    timeline: state.timeline
      ? { ...state.timeline, epochs: choices.filter((c) => c.type === 'NEpochSlot') }
      : undefined,
    crystalSphere: state.crystalSphere
      ? {
          ...state.crystalSphere,
          cells: choices.filter((c) => c.type === 'NCrystalSphereCell'),
          tools: choices.filter((c) => c.type === 'NDivinationButton'),
        }
      : undefined,
    choices:
      kind === 'treasure'
        ? choices.filter((c) => c.type !== 'NTreasureButton')
        : kind === 'map'
          ? choices.filter((c) => c.enabled && c.type.includes('MapPoint'))
          : choices,
    offers: offers.length || kind === 'treasure' ? offers : undefined,
    canDecline:
      kind === 'treasure'
        ? choices.some((c) => c.enabled && c.type === 'NProceedButton')
        : undefined,
    text: kind === 'treasure' ? [] : state.text,
    map: state.map,
    nextRooms: state.map?.filter((p) => p.state === 'Travelable'),
  };
}
