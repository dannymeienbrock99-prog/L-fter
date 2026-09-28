'use strict';
let ws, uuid, action, settings = {};
const $ = id => document.getElementById(id);
function send(event, payload) { if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify({event,context:uuid,action,payload})); }
function requestCatalog() { send('sendToPlugin', {command:'catalog'}); }
function fill(id, items, selected, placeholder) {
  const control=$(id); control.replaceChildren(new Option(placeholder,''));
  for(const item of items || []) control.add(new Option(item.name,item.id));
  if(selected && ![...control.options].some(o=>o.value===selected)) control.add(new Option('Gespeicherte Auswahl (nicht verfügbar)',selected));
  control.value=selected || '';
}
function save() { settings={...settings,tileId:$('tile').value,mode:$('mode').value,curveId:$('curveId').value}; send('setSettings',settings); }
window.connectElgatoStreamDeckSocket = (port, propertyInspectorUUID, registerEvent, info, actionInfo) => {
  uuid=propertyInspectorUUID; const data=JSON.parse(actionInfo); action=data.action; settings=data.payload?.settings || {};
  const isCurve=action.endsWith('.curve'); $('fan').hidden=isCurve; $('curve').hidden=!isCurve; $('mode').value=settings.mode || 'temperature';
  ws=new WebSocket(`ws://127.0.0.1:${port}`);
  ws.onopen=()=>{ws.send(JSON.stringify({event:registerEvent,uuid}));requestCatalog();};
  ws.onmessage=message=>{const e=JSON.parse(message.data); if(e.event!=='sendToPropertyInspector')return;
    const p=e.payload || {}, catalog=p.catalog || {};
    $('status').textContent=p.online?'Mit FanAtlas verbunden':'FanAtlas ist nicht erreichbar. Bitte die App öffnen.';
    fill('tile',catalog.scene?.tiles,settings.tileId,'Lüfter wählen …'); fill('curveId',catalog.curves,settings.curveId,'Kurve wählen …');
  };
  ws.onclose=()=>{$('status').textContent='Verbindung zu Stream Deck geschlossen.';};
};
for(const id of ['tile','mode','curveId']) $(id).addEventListener('change',save);
$('reload').addEventListener('click',requestCatalog);

