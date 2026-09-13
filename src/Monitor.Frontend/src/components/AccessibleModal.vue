<template>
  <dialog
    ref="dialogElement"
    class="accessible-modal"
    :aria-labelledby="titleId"
    :aria-describedby="description ? descriptionId : undefined"
    @cancel="handleCancel"
    @click.self="handleBackdropClick"
  >
    <h2 :id="titleId" class="sr-only">{{ title }}</h2>
    <p v-if="description" :id="descriptionId" class="sr-only">{{ description }}</p>
    <slot />
  </dialog>
</template>

<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, useId, watch } from 'vue';

const props = withDefaults(defineProps<{
  open: boolean;
  title: string;
  description?: string;
  returnFocus?: HTMLElement | null;
}>(), {
  description: ''
});

const emit = defineEmits<{
  close: [];
}>();

const dialogElement = ref<HTMLDialogElement | null>(null);
const idPrefix = useId().replaceAll(':', '');
const titleId = `modal-title-${idPrefix}`;
const descriptionId = `modal-description-${idPrefix}`;
let previousBodyOverflow = '';
let bodyLocked = false;
let closeEventEmitted = false;

watch(() => props.open, (open) => {
  if (open) {
    void openDialog();
  } else {
    closeDialog(false);
  }
}, { immediate: true });

async function openDialog() {
  await nextTick();
  const dialog = dialogElement.value;
  if (!dialog || dialog.open) return;

  closeEventEmitted = false;
  previousBodyOverflow = document.body.style.overflow;
  document.body.style.overflow = 'hidden';
  bodyLocked = true;
  dialog.showModal();
  const firstFocusable = dialog.querySelector<HTMLElement>('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])');
  (firstFocusable ?? dialog).focus();
}

function handleCancel(event: Event) {
  event.preventDefault();
  closeDialog(true);
}

function handleBackdropClick() {
  closeDialog(true);
}

function closeDialog(emitClose: boolean) {
  const dialog = dialogElement.value;
  if (dialog?.open) {
    dialog.close();
  }

  restoreFocus();
  if (emitClose && !closeEventEmitted) {
    closeEventEmitted = true;
    emit('close');
  }
}

function restoreFocus() {
  if (bodyLocked) {
    document.body.style.overflow = previousBodyOverflow;
    previousBodyOverflow = '';
    bodyLocked = false;
  }

  const target = props.returnFocus;
  if (target?.isConnected) {
    target.focus();
  }
}

onBeforeUnmount(() => {
  closeDialog(false);
});
</script>
