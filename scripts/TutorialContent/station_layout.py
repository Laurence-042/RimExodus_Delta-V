"""Shared geometry, trade references and solar sizing for station prefabs."""
import math
import xml.etree.ElementTree as ET


FACING = ((0, 1), (1, 0), (0, -1), (-1, 0))


def rectangle(bounds):
    x0, z0, x1, z1 = bounds
    return {(x, z) for x in range(x0, x1+1) for z in range(z0, z1+1)}


def center(cells):
    return ((min(x for x, _ in cells) + max(x for x, _ in cells))/2,
            (min(z for _, z in cells) + max(z for _, z in cells))/2)


def furnishing_grid(area, count):
    x0, x1 = min(x for x, _ in area), max(x for x, _ in area)
    z0, z1 = min(z for _, z in area), max(z for _, z in area)
    columns = min(3, math.ceil(math.sqrt(count)))
    rows = math.ceil(count/columns)
    return [(x0+(x1-x0)*(i % columns+.5)/columns, z0+(z1-z0)*(i // columns+.5)/rows)
            for i in range(count)]


def prepare_trade_state(record, state, field, *, indiscriminate_supply):
    cargo = record['stateOverrides']
    supply = state.find('supplyCargo')
    if supply is None:
        supply = ET.SubElement(state, 'supplyCargo')
    for child in list(supply):
        supply.remove(child)
    # Scribe_References uses Thing.GetUniqueLoadID(), not the ThingID saved in <id>.
    for cabinet in cargo['_supplyCargo']:
        ET.SubElement(supply, 'li').text = 'Thing_' + cabinet['_loadId']
    field(state, 'deliveryCargo', 'Thing_' + cargo['_deliveryCargo']['_loadId'])
    field(state, 'traderKind', 'Orbital_BulkGoods')
    field(state, 'restockDays', 15)
    field(state, 'lastRestockTick', -1)
    field(state, 'tradeStockGenerated', 'False')
    field(state, 'indiscriminateSupply', str(indiscriminate_supply))
    field(state, 'tradePriceSeed', 7319)


def solar_budget(demand, panel_watts, capacity, efficiency, *, panel_step=1):
    # GenCelestial, at 45 degrees north on day 0 (winter solstice). At this
    # latitude the seasonal offset is 0.2 and both rotation corrections apply.
    samples = 240
    lat = math.radians(45)
    correction = math.radians(19 + 17*(60-45)/60)
    glow = []
    for i in range(samples):
        angle = 2*math.pi*((i+.5)/samples-.5)
        dot = (math.cos(lat)*math.cos(angle) - .2*math.sin(lat)) / math.sqrt(1.04)
        incidence = math.cos(max(0, math.acos(dot)-correction))
        glow.append(max(0, min(1, incidence/.7)))
    # Keep the budget valid after losing one panel and one battery. Charge loss
    # and 5 W of native self-discharge per connected battery count toward demand.
    for panels in range(max(2, panel_step), 100, panel_step):
        batteries = 2
        while True:
            balance = [(panels-1)*panel_watts*g - demand - 5*batteries for g in glow]
            deficit = sum(max(0, -w) for w in balance) / samples
            needed = math.ceil(deficit*1.15/capacity) + 1
            if needed <= batteries:
                break
            batteries = needed
        recharge = sum(max(0, w) for w in balance) * efficiency / samples
        if recharge >= deficit * 1.1:
            return dict(panels=panels, batteries=batteries, deficitWd=round(deficit),
                        rechargeWd=round(recharge), capacityWd=(batteries-1)*capacity)
    raise ValueError('station solar budget exceeds 99 panels')
