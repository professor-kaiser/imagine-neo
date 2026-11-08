'use strict';
const comparison = document.getElementById('comparison');
const position = document.getElementById('position');
const output = document.getElementById('position-value');
const effect = document.getElementById('effect');
const result = document.getElementById('result');
const note = document.getElementById('effect-note');

function updatePosition() {
  const value = Number(position.value);
  comparison.style.setProperty('--split', value + '%');
  output.value = value + ' %';
}
position.addEventListener('input', updatePosition);
effect.addEventListener('change', () => {
  const grayscale = effect.value === 'grayscale';
  result.src = grayscale ? 'assets/grayscale.png' : 'assets/denoise.png';
  result.alt = grayscale ? 'Après : conversion en niveaux de gris.' : 'Après : résultat du plugin de débruitage.';
  note.textContent = grayscale
    ? 'Conversion OpenCV en niveaux de gris sur toute l’image ; transparence conservée.'
    : 'Débruitage non-local means : le bruit est réduit dans la région centrale ; les pixels hors sélection restent identiques.';
});
let dragging = false;
function moveDivider(event) {
  if (!dragging) return;
  const bounds = comparison.getBoundingClientRect();
  position.value = String(Math.round(Math.max(0, Math.min(100, (event.clientX - bounds.left) / bounds.width * 100))));
  updatePosition();
}
comparison.addEventListener('pointerdown', event => {
  if (event.button !== 0) return;
  dragging = true;
  comparison.setPointerCapture(event.pointerId);
  moveDivider(event);
});
comparison.addEventListener('pointermove', moveDivider);
comparison.addEventListener('pointerup', () => { dragging = false; });
comparison.addEventListener('pointercancel', () => { dragging = false; });
comparison.addEventListener('lostpointercapture', () => { dragging = false; });
updatePosition();
